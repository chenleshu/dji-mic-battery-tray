using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DjiMicBattery
{
    public sealed class TransmitterState
    {
        public int Slot { get; set; }
        public bool Connected { get; set; }
        public int? BatteryGauge { get; set; }
        public bool Charging { get; set; }
    }

    public sealed class ReaderResult
    {
        public string Status { get; set; }
        public string Message { get; set; }
        public int? ProtocolVersion { get; set; }
        public List<TransmitterState> Transmitters { get; set; }
        public string SampledAt { get; set; }

        public ReaderResult()
        {
            Status = "reader_error";
            Message = "未知读取错误";
            Transmitters = new List<TransmitterState>();
            SampledAt = DateTime.UtcNow.ToString("o");
        }
    }

    public static class Reader
    {
        private const string DeviceRegistryPath = @"SYSTEM\CurrentControlSet\Enum\USB\VID_2CA3&PID_4011&MI_06";
        private const string DevicePathNeedle = "vid_2ca3&pid_4011&mi_06";
        private const byte BulkInEndpoint = 0x86;

        public static ReaderResult Read(int timeoutMilliseconds)
        {
            try
            {
                List<Guid> interfaceGuids = ReadInterfaceGuids();
                if (interfaceGuids.Count == 0)
                {
                    return Result(
                        "setup_required",
                        "DJI Mic Mini 数据接口尚未绑定 WinUSB。请只为 Interface 6 安装 WinUSB 驱动。"
                    );
                }

                List<string> paths = interfaceGuids
                    .SelectMany(EnumerateInterfacePaths)
                    .Where(path => path.IndexOf(DevicePathNeedle, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (paths.Count == 0)
                {
                    return Result(
                        "setup_required",
                        "检测到 DJI 数据接口，但 Windows 尚未公开可访问的 WinUSB 设备路径。"
                    );
                }

                Exception last = null;
                foreach (string path in paths)
                {
                    try
                    {
                        return ReadPath(path, Math.Max(800, timeoutMilliseconds));
                    }
                    catch (Exception exc)
                    {
                        last = exc;
                    }
                }

                return Result("reader_error", last == null ? "无法打开 DJI 数据接口" : last.Message);
            }
            catch (Exception exc)
            {
                return Result("reader_error", exc.Message);
            }
        }

        public static string[] InterfacePathsForDiagnostics()
        {
            return ReadInterfaceGuids()
                .SelectMany(guid => EnumerateInterfacePaths(guid)
                    .Select(path => guid.ToString("B") + "|" + path))
                .ToArray();
        }

        public static ReaderResult DecodeFramesForTest(byte[][] frames)
        {
            ReaderResult latest = null;
            foreach (byte[] frame in frames)
            {
                ReaderResult decoded = DecodeFrame(frame);
                if (decoded == null)
                {
                    continue;
                }
                latest = decoded;
                if (decoded.Status == "unsupported_firmware" || HasBattery(decoded))
                {
                    return decoded;
                }
            }
            return latest ?? Result("no_data", "未找到有效的 DJI 状态帧");
        }

        private static ReaderResult ReadPath(string path, int timeoutMilliseconds)
        {
            IntPtr file = CreateFile(
                path,
                GenericRead | GenericWrite,
                FileShareRead | FileShareWrite,
                IntPtr.Zero,
                OpenExisting,
                FileFlagOverlapped,
                IntPtr.Zero
            );
            if (file == InvalidHandleValue)
            {
                int error = Marshal.GetLastWin32Error();
                Win32Exception native = new Win32Exception(error);
                throw new IOException(
                    "无法打开 DJI WinUSB 数据接口（Windows 错误 " + error + "：" + native.Message + "；路径：" + path + "）"
                );
            }

            IntPtr usb = IntPtr.Zero;
            try
            {
                if (!WinUsb_Initialize(file, out usb))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUSB 初始化失败");
                }

                uint pipeTimeout = 1000;
                WinUsb_SetPipePolicy(usb, BulkInEndpoint, PipeTransferTimeout, 4, ref pipeTimeout);

                Stopwatch timer = Stopwatch.StartNew();
                List<byte> stream = new List<byte>(1024);
                ReaderResult latest = null;

                while (timer.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    int remaining = Math.Max(100, timeoutMilliseconds - (int)timer.ElapsedMilliseconds);
                    byte[] chunk = ReadPipe(file, usb, BulkInEndpoint, Math.Min(1200, remaining));
                    if (chunk == null || chunk.Length == 0)
                    {
                        continue;
                    }

                    stream.AddRange(chunk);
                    byte[] frame;
                    while ((frame = TakeFrame(stream)) != null)
                    {
                        ReaderResult decoded = DecodeFrame(frame);
                        if (decoded == null)
                        {
                            continue;
                        }
                        latest = decoded;
                        if (decoded.Status == "unsupported_firmware" || HasBattery(decoded))
                        {
                            return decoded;
                        }
                        if (decoded.Status == "ok" && decoded.Transmitters.All(tx => !tx.Connected))
                        {
                            return decoded;
                        }
                    }
                }

                return latest ?? Result(
                    "no_data",
                    "DJI 接收器已打开，但在超时时间内没有收到状态数据；请确认接收器和发射器已开机。"
                );
            }
            finally
            {
                if (usb != IntPtr.Zero)
                {
                    WinUsb_Free(usb);
                }
                CloseHandle(file);
            }
        }

        private static bool HasBattery(ReaderResult result)
        {
            return result.Transmitters.Any(tx => tx.Connected && tx.BatteryGauge.HasValue && tx.BatteryGauge.Value > 0);
        }

        private static ReaderResult DecodeFrame(byte[] frame)
        {
            if (frame == null || frame.Length < 12 || frame[0] != 0x55 || frame[2] != 0x04)
            {
                return null;
            }
            if (frame[8] != 0x00 || frame[9] != 0x5b || frame[10] != 0x03)
            {
                return null;
            }

            if (frame[11] == 0x00)
            {
                ReaderResult v1 = Result(
                    "unsupported_firmware",
                    "接收器正在使用 v1 协议；该版本的 USB 电量字段尚未被可靠识别。"
                );
                v1.ProtocolVersion = 1;
                return v1;
            }
            if (frame[11] != 0x03)
            {
                return null;
            }

            const int headerLength = 52;
            const int slotLength = 32;
            if (frame.Length < headerLength + 2)
            {
                return null;
            }
            int remainder = frame.Length - (headerLength + 2);
            if (remainder < 0 || remainder % slotLength != 0)
            {
                return null;
            }

            int slotCount = Math.Min(2, remainder / slotLength);
            byte connected = frame[44];
            ReaderResult result = Result("ok", "DJI Mic Mini 状态读取成功");
            result.ProtocolVersion = 2;
            for (int i = 0; i < 2; i++)
            {
                result.Transmitters.Add(new TransmitterState
                {
                    Slot = i + 1,
                    Connected = (connected & (1 << i)) != 0,
                    BatteryGauge = null,
                    Charging = false
                });
            }

            for (int slotPosition = 0; slotPosition < slotCount; slotPosition++)
            {
                int offset = headerLength + slotLength * slotPosition;
                int unit = frame[offset + 1];
                if (unit < 1 || unit > 2)
                {
                    continue;
                }
                TransmitterState tx = result.Transmitters[unit - 1];
                if (!tx.Connected)
                {
                    continue;
                }
                byte flags = frame[offset + 7];
                tx.Charging = (flags & 0x02) != 0;
                tx.BatteryGauge = (flags >> 2) & 0x07;
            }
            return result;
        }

        private static byte[] TakeFrame(List<byte> buffer)
        {
            while (true)
            {
                int start = -1;
                for (int i = 0; i < buffer.Count; i++)
                {
                    if (buffer[i] != 0x55)
                    {
                        continue;
                    }
                    if (i + 2 >= buffer.Count)
                    {
                        if (i > 0)
                        {
                            buffer.RemoveRange(0, i);
                        }
                        return null;
                    }
                    if (buffer[i + 2] == 0x04)
                    {
                        start = i;
                        break;
                    }
                }

                if (start < 0)
                {
                    bool keepSof = buffer.Count > 0 && buffer[buffer.Count - 1] == 0x55;
                    buffer.Clear();
                    if (keepSof)
                    {
                        buffer.Add(0x55);
                    }
                    return null;
                }
                if (start > 0)
                {
                    buffer.RemoveRange(0, start);
                }

                int length = buffer[1];
                if (length < 4 || length > 256)
                {
                    buffer.RemoveAt(0);
                    continue;
                }
                if (buffer.Count < length)
                {
                    return null;
                }
                byte[] frame = buffer.GetRange(0, length).ToArray();
                buffer.RemoveRange(0, length);
                return frame;
            }
        }

        private static ReaderResult Result(string status, string message)
        {
            return new ReaderResult
            {
                Status = status,
                Message = message,
                ProtocolVersion = null,
                Transmitters = new List<TransmitterState>(),
                SampledAt = DateTime.UtcNow.ToString("o")
            };
        }

        private static List<Guid> ReadInterfaceGuids()
        {
            HashSet<Guid> guids = new HashSet<Guid>();
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(DeviceRegistryPath))
            {
                if (root == null)
                {
                    return guids.ToList();
                }
                foreach (string instanceName in root.GetSubKeyNames())
                {
                    using (RegistryKey parameters = root.OpenSubKey(instanceName + @"\Device Parameters"))
                    {
                        if (parameters == null)
                        {
                            continue;
                        }
                        AddGuids(guids, parameters.GetValue("DeviceInterfaceGUIDs"));
                        AddGuids(guids, parameters.GetValue("DeviceInterfaceGUID"));
                    }
                }
            }

            // Standard USB-device interface class. Some WinUSB packages use it
            // instead of adding a per-device DeviceInterfaceGUIDs value.
            guids.Add(new Guid("A5DCBF10-6530-11D2-901F-00C04FB951ED"));
            return guids.ToList();
        }

        private static void AddGuids(HashSet<Guid> target, object raw)
        {
            if (raw is string)
            {
                Guid value;
                if (Guid.TryParse((string)raw, out value))
                {
                    target.Add(value);
                }
                return;
            }
            string[] values = raw as string[];
            if (values == null)
            {
                return;
            }
            foreach (string text in values)
            {
                Guid value;
                if (Guid.TryParse(text, out value))
                {
                    target.Add(value);
                }
            }
        }

        private static IEnumerable<string> EnumerateInterfacePaths(Guid classGuid)
        {
            IntPtr set = SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (set == InvalidHandleValue)
            {
                yield break;
            }

            try
            {
                uint index = 0;
                while (true)
                {
                    SP_DEVICE_INTERFACE_DATA data = new SP_DEVICE_INTERFACE_DATA();
                    data.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref classGuid, index, ref data))
                    {
                        if (Marshal.GetLastWin32Error() == ErrorNoMoreItems)
                        {
                            yield break;
                        }
                        index++;
                        continue;
                    }

                    uint required = 0;
                    SP_DEVINFO_DATA deviceInfo = new SP_DEVINFO_DATA();
                    deviceInfo.cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out required, ref deviceInfo);
                    if (required == 0)
                    {
                        index++;
                        continue;
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)required);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, detail, required, out required, ref deviceInfo))
                        {
                            string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                            if (!string.IsNullOrWhiteSpace(path))
                            {
                                yield return path;
                            }
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                    index++;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
        }

        private static byte[] ReadPipe(IntPtr file, IntPtr usb, byte endpoint, int timeoutMilliseconds)
        {
            IntPtr buffer = Marshal.AllocHGlobal(512);
            IntPtr done = CreateEvent(IntPtr.Zero, true, false, null);
            if (done == IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 USB 读取事件");
            }

            try
            {
                NATIVE_OVERLAPPED overlapped = new NATIVE_OVERLAPPED();
                overlapped.EventHandle = done;
                uint transferred;
                bool completed = WinUsb_ReadPipe(usb, endpoint, buffer, 512, out transferred, ref overlapped);
                if (!completed)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ErrorIoPending)
                    {
                        throw new Win32Exception(error, "读取 DJI USB 数据失败");
                    }
                    uint wait = WaitForSingleObject(done, (uint)Math.Max(1, timeoutMilliseconds));
                    if (wait == WaitTimeout)
                    {
                        CancelIoEx(file, ref overlapped);
                        WaitForSingleObject(done, 250);
                        return null;
                    }
                    if (wait != WaitObject0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "等待 DJI USB 数据失败");
                    }
                    if (!WinUsb_GetOverlappedResult(usb, ref overlapped, out transferred, false))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "完成 DJI USB 读取失败");
                    }
                }

                if (transferred == 0)
                {
                    return null;
                }
                byte[] data = new byte[transferred];
                Marshal.Copy(buffer, data, 0, (int)transferred);
                return data;
            }
            finally
            {
                CloseHandle(done);
                Marshal.FreeHGlobal(buffer);
            }
        }

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const uint PipeTransferTimeout = 0x03;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorIoPending = 997;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public int cbSize;
            public Guid ClassGuid;
            public int DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NATIVE_OVERLAPPED
        {
            public IntPtr InternalLow;
            public IntPtr InternalHigh;
            public uint OffsetLow;
            public uint OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr deviceInfo, ref Guid classGuid, uint index, ref SP_DEVICE_INTERFACE_DATA data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint detailSize, out uint requiredSize, ref SP_DEVINFO_DATA deviceInfo);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr file, ref NATIVE_OVERLAPPED overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(IntPtr file, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType, uint valueLength, ref uint value);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength, out uint lengthTransferred, ref NATIVE_OVERLAPPED overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetOverlappedResult(IntPtr interfaceHandle, ref NATIVE_OVERLAPPED overlapped, out uint lengthTransferred, bool wait);
    }
}
