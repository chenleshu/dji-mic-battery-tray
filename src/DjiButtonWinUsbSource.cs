using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DjiMicBattery
{
    internal static class DjiButtonProtocol
    {
        public static DjiButtonReportState ParseReport(byte[] report)
        {
            if (report == null || report.Length != 3 || report[0] != 0x06 || report[2] != 0x00)
            {
                return DjiButtonReportState.Ignore;
            }

            if (report[1] == 0x01)
            {
                return DjiButtonReportState.Press;
            }
            if (report[1] == 0x00)
            {
                return DjiButtonReportState.Release;
            }
            return DjiButtonReportState.Ignore;
        }

        public static string FormatReport(byte[] report)
        {
            return report == null
                ? ""
                : string.Join("-", report.Select(value => value.ToString("X2")).ToArray());
        }
    }

    internal sealed class DjiButtonWinUsbSource : IDisposable
    {
        private const string DevicePathNeedle = "vid_2ca3&pid_4011&mi_00";
        private const string DeviceRegistryPath = @"SYSTEM\CurrentControlSet\Enum\USB\VID_2CA3&PID_4011&MI_00";
        private const int RefreshMilliseconds = 800;
        private const int ErrorNotFound = 1168;
        private static readonly Guid ButtonInterfaceGuid = new Guid("8C54EF11-F475-410E-9F4D-380F74EE5C24");

        private readonly Control dispatcher;
        private readonly AutoResetEvent refreshEvent;
        private readonly object stateLock;
        private readonly Dictionary<string, DeviceReader> readers;
        private readonly Dictionary<string, string> connectedDevices;
        private readonly Thread managerThread;
        private volatile bool stopping;
        private bool driverReady;
        private int errorCode;
        private string lastErrorStage;
        private string lastKnownPath;

        public event Action<DjiButtonReportState, string, string> InputReceived;
        public event Action DevicesChanged;

        public bool Available
        {
            get
            {
                lock (stateLock)
                {
                    return driverReady && errorCode == 0;
                }
            }
        }

        public bool DriverReady
        {
            get
            {
                lock (stateLock)
                {
                    return driverReady;
                }
            }
        }

        public bool HasDjiDevice
        {
            get
            {
                lock (stateLock)
                {
                    return connectedDevices.Count > 0;
                }
            }
        }

        public int ConnectedDeviceCount
        {
            get
            {
                lock (stateLock)
                {
                    return connectedDevices.Count;
                }
            }
        }

        public string DjiDevicePath
        {
            get
            {
                lock (stateLock)
                {
                    if (connectedDevices.Count > 0)
                    {
                        return string.Join(" | ", connectedDevices.Keys.ToArray());
                    }
                    return lastKnownPath ?? "";
                }
            }
        }

        public string EndpointSummary
        {
            get
            {
                lock (stateLock)
                {
                    return connectedDevices.Count == 0
                        ? ""
                        : string.Join(",", connectedDevices.Values.Distinct().ToArray());
                }
            }
        }

        public int ErrorCode
        {
            get
            {
                lock (stateLock)
                {
                    return errorCode;
                }
            }
        }

        public string LastErrorStage
        {
            get
            {
                lock (stateLock)
                {
                    return lastErrorStage ?? "";
                }
            }
        }

        public DjiButtonWinUsbSource(Control dispatcher)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException("dispatcher");
            }

            this.dispatcher = dispatcher;
            refreshEvent = new AutoResetEvent(false);
            stateLock = new object();
            readers = new Dictionary<string, DeviceReader>(StringComparer.OrdinalIgnoreCase);
            connectedDevices = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            lastKnownPath = "";
            lastErrorStage = "";
            errorCode = ErrorNotFound;

            managerThread = new Thread(ManageDevices);
            managerThread.IsBackground = true;
            managerThread.Name = "DJI MI_00 WinUSB manager";
            managerThread.Start();
        }

        private void ManageDevices()
        {
            while (!stopping)
            {
                try
                {
                    List<string> paths = ReadInterfaceGuids()
                        .SelectMany(EnumerateInterfacePaths)
                        .Where(path => path.IndexOf(DevicePathNeedle, StringComparison.OrdinalIgnoreCase) >= 0)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    bool ready = paths.Count > 0;

                    ReconcileReaders(paths);
                    lock (stateLock)
                    {
                        driverReady = ready;
                        if (paths.Count > 0)
                        {
                            lastKnownPath = paths[0];
                        }
                        if (!ready)
                        {
                            errorCode = ErrorNotFound;
                        }
                        else if (connectedDevices.Count > 0)
                        {
                            errorCode = 0;
                        }
                    }
                }
                catch (Exception exception)
                {
                    Win32Exception win32 = exception as Win32Exception;
                    lock (stateLock)
                    {
                        errorCode = win32 == null ? Marshal.GetHRForException(exception) : win32.NativeErrorCode;
                    }
                }

                refreshEvent.WaitOne(RefreshMilliseconds);
            }

            foreach (DeviceReader reader in readers.Values.ToArray())
            {
                reader.Dispose();
            }
            readers.Clear();
        }

        private void ReconcileReaders(List<string> paths)
        {
            HashSet<string> current = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            foreach (string path in readers.Keys.ToArray())
            {
                DeviceReader reader = readers[path];
                if (!current.Contains(path) || !reader.IsRunning)
                {
                    if (reader.StopAndWait(5000))
                    {
                        readers.Remove(path);
                        SetReaderDisconnected(path, 0, "");
                    }
                }
            }

            foreach (string path in paths)
            {
                if (readers.ContainsKey(path))
                {
                    continue;
                }
                DeviceReader reader = new DeviceReader(this, path);
                readers.Add(path, reader);
                reader.Start();
            }
        }

        private void SetReaderConnected(string path, byte endpoint, ushort maximumPacketSize)
        {
            bool changed;
            lock (stateLock)
            {
                string value = "0x" + endpoint.ToString("X2") + "/" + maximumPacketSize + "B";
                string existing;
                changed = !connectedDevices.TryGetValue(path, out existing) || existing != value;
                connectedDevices[path] = value;
                lastKnownPath = path;
                driverReady = true;
                errorCode = 0;
                lastErrorStage = "";
            }
            if (changed)
            {
                PostDevicesChanged();
            }
        }

        private void SetReaderDisconnected(string path, int readerError, string stage)
        {
            bool changed;
            lock (stateLock)
            {
                changed = connectedDevices.Remove(path);
                if (connectedDevices.Count == 0 && readerError != 0)
                {
                    errorCode = readerError;
                    lastErrorStage = stage ?? "";
                }
            }
            if (changed)
            {
                PostDevicesChanged();
            }
        }

        private void PostInput(DjiButtonReportState state, string path, byte[] report)
        {
            string reportHex = DjiButtonProtocol.FormatReport(report);
            Post(delegate
            {
                Action<DjiButtonReportState, string, string> handler = InputReceived;
                if (handler != null)
                {
                    handler(state, path, reportHex);
                }
            });
        }

        private void PostDevicesChanged()
        {
            Post(delegate
            {
                Action handler = DevicesChanged;
                if (handler != null)
                {
                    handler();
                }
            });
        }

        private void Post(MethodInvoker action)
        {
            try
            {
                if (!stopping && !dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                {
                    dispatcher.BeginInvoke(action);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        public void Dispose()
        {
            if (stopping)
            {
                return;
            }
            stopping = true;
            refreshEvent.Set();
            bool managerStopped = true;
            if (Thread.CurrentThread != managerThread)
            {
                managerStopped = managerThread.Join(5000);
            }
            if (managerStopped)
            {
                refreshEvent.Dispose();
            }
            GC.SuppressFinalize(this);
        }

        private sealed class DeviceReader : IDisposable
        {
            private readonly DjiButtonWinUsbSource owner;
            private readonly string path;
            private readonly Thread thread;
            private readonly object handleLock;
            private volatile bool stopping;
            private IntPtr activeFile;

            public bool IsRunning
            {
                get { return thread.IsAlive && !stopping; }
            }

            public DeviceReader(DjiButtonWinUsbSource owner, string path)
            {
                this.owner = owner;
                this.path = path;
                handleLock = new object();
                activeFile = IntPtr.Zero;
                thread = new Thread(ReadLoop);
                thread.IsBackground = true;
                thread.Name = "DJI MI_00 WinUSB reader";
            }

            public void Start()
            {
                thread.Start();
            }

            private void ReadLoop()
            {
                string stage = "CreateFile";
                IntPtr file = CreateFile(
                    path,
                    GenericRead | GenericWrite,
                    FileShareRead | FileShareWrite,
                    IntPtr.Zero,
                    OpenExisting,
                    FileAttributeNormal | FileFlagOverlapped,
                    IntPtr.Zero
                );
                if (file == InvalidHandleValue)
                {
                    owner.SetReaderDisconnected(path, Marshal.GetLastWin32Error(), stage);
                    return;
                }
                lock (handleLock)
                {
                    activeFile = file;
                }

                IntPtr usb = IntPtr.Zero;
                try
                {
                    stage = "WinUsb_Initialize";
                    if (!WinUsb_Initialize(file, out usb))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUSB 初始化 DJI MI_00 失败");
                    }

                    stage = "WinUsb_QueryInterfaceSettings";
                    USB_INTERFACE_DESCRIPTOR descriptor;
                    if (!WinUsb_QueryInterfaceSettings(usb, 0, out descriptor))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "读取 DJI MI_00 接口描述符失败");
                    }

                    stage = "WinUsb_QueryPipe";
                    WINUSB_PIPE_INFORMATION pipe = FindInterruptInPipe(usb, descriptor.NumberOfEndpoints);
                    owner.SetReaderConnected(path, pipe.PipeId, pipe.MaximumPacketSize);
                    int bufferLength = Math.Max(1, (int)pipe.MaximumPacketSize);
                    while (!stopping)
                    {
                        stage = "WinUsb_ReadPipe";
                        byte[] report = ReadPacket(file, usb, pipe.PipeId, bufferLength);
                        if (report == null || report.Length == 0)
                        {
                            continue;
                        }
                        DjiButtonReportState reportState = DjiButtonProtocol.ParseReport(report);
                        if (reportState != DjiButtonReportState.Ignore)
                        {
                            owner.PostInput(reportState, path, report);
                        }
                    }
                }
                catch (Win32Exception exception)
                {
                    owner.SetReaderDisconnected(path, exception.NativeErrorCode, stage);
                }
                catch (Exception exception)
                {
                    owner.SetReaderDisconnected(path, Marshal.GetHRForException(exception), stage);
                }
                finally
                {
                    owner.SetReaderDisconnected(path, 0, "");
                    lock (handleLock)
                    {
                        activeFile = IntPtr.Zero;
                    }
                    if (usb != IntPtr.Zero)
                    {
                        WinUsb_Free(usb);
                    }
                    CloseHandle(file);
                }
            }

            public void Dispose()
            {
                StopAndWait(5000);
            }

            public bool StopAndWait(int milliseconds)
            {
                if (!stopping)
                {
                    stopping = true;
                    lock (handleLock)
                    {
                        if (activeFile != IntPtr.Zero && activeFile != InvalidHandleValue)
                        {
                            CancelIoEx(activeFile, IntPtr.Zero);
                        }
                    }
                }
                if (Thread.CurrentThread == thread)
                {
                    return false;
                }
                return thread.Join(milliseconds);
            }

            private byte[] ReadPacket(IntPtr file, IntPtr usb, byte pipeId, int bufferLength)
            {
                IntPtr buffer = Marshal.AllocHGlobal(bufferLength);
                IntPtr overlappedPointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(NATIVE_OVERLAPPED)));
                IntPtr completedEvent = CreateEvent(IntPtr.Zero, true, false, null);
                if (completedEvent == IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(overlappedPointer);
                    Marshal.FreeHGlobal(buffer);
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "创建 DJI MI_00 读取事件失败");
                }

                bool pending = false;
                bool terminal = false;
                try
                {
                    NATIVE_OVERLAPPED overlapped = new NATIVE_OVERLAPPED();
                    overlapped.EventHandle = completedEvent;
                    Marshal.StructureToPtr(overlapped, overlappedPointer, false);
                    uint transferred;
                    bool completed = WinUsb_ReadPipe(
                        usb,
                        pipeId,
                        buffer,
                        (uint)bufferLength,
                        out transferred,
                        overlappedPointer
                    );
                    if (!completed)
                    {
                        int startError = Marshal.GetLastWin32Error();
                        if (startError != ErrorIoPending)
                        {
                            if (stopping && (startError == ErrorOperationAborted || startError == ErrorInvalidHandle))
                            {
                                return null;
                            }
                            throw new Win32Exception(startError, "启动 DJI MI_00 异步读取失败");
                        }
                        pending = true;

                        while (!stopping)
                        {
                            uint wait = WaitForSingleObject(completedEvent, 250);
                            if (wait == WaitObject0)
                            {
                                break;
                            }
                            if (wait != WaitTimeout)
                            {
                                throw new Win32Exception(Marshal.GetLastWin32Error(), "等待 DJI MI_00 按键报告失败");
                            }
                        }

                        if (stopping)
                        {
                            CancelIoEx(file, overlappedPointer);
                        }
                        bool result = WinUsb_GetOverlappedResult(usb, overlappedPointer, out transferred, true);
                        terminal = true;
                        if (!result)
                        {
                            int resultError = Marshal.GetLastWin32Error();
                            if (stopping && (resultError == ErrorOperationAborted || resultError == ErrorInvalidHandle))
                            {
                                return null;
                            }
                            throw new Win32Exception(resultError, "完成 DJI MI_00 异步读取失败");
                        }
                    }

                    if (transferred == 0)
                    {
                        return null;
                    }
                    byte[] report = new byte[transferred];
                    Marshal.Copy(buffer, report, 0, (int)transferred);
                    return report;
                }
                finally
                {
                    if (pending && !terminal)
                    {
                        CancelIoEx(file, overlappedPointer);
                        uint ignored;
                        WinUsb_GetOverlappedResult(usb, overlappedPointer, out ignored, true);
                    }
                    CloseHandle(completedEvent);
                    Marshal.FreeHGlobal(overlappedPointer);
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }

        internal static WINUSB_PIPE_INFORMATION FindInterruptInPipe(IntPtr usb, byte endpointCount)
        {
            for (byte index = 0; index < endpointCount; index++)
            {
                WINUSB_PIPE_INFORMATION pipe;
                if (!WinUsb_QueryPipe(usb, 0, index, out pipe))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "枚举 DJI MI_00 端点失败");
                }
                if (pipe.PipeType == UsbdPipeTypeInterrupt && (pipe.PipeId & 0x80) != 0)
                {
                    return pipe;
                }
            }
            throw new IOException("DJI MI_00 没有可读取的中断 IN 端点。");
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

        private static IEnumerable<Guid> ReadInterfaceGuids()
        {
            HashSet<Guid> guids = new HashSet<Guid>();
            // Keep the GUID used by the locally verified MI_00 binding, while also
            // accepting the per-device GUID generated by maintained WinUSB tools
            // such as Zadig/libwdi. Paths are still filtered to the exact DJI MI_00.
            guids.Add(ButtonInterfaceGuid);

            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(DeviceRegistryPath))
            {
                if (root == null)
                {
                    return guids;
                }
                foreach (string instanceName in root.GetSubKeyNames())
                {
                    using (RegistryKey parameters = root.OpenSubKey(instanceName + @"\Device Parameters"))
                    {
                        if (parameters == null)
                        {
                            continue;
                        }
                        AddInterfaceGuids(guids, parameters.GetValue("DeviceInterfaceGUIDs"));
                        AddInterfaceGuids(guids, parameters.GetValue("DeviceInterfaceGUID"));
                    }
                }
            }
            return guids;
        }

        private static void AddInterfaceGuids(HashSet<Guid> guids, object raw)
        {
            string single = raw as string;
            if (single != null)
            {
                Guid parsed;
                if (Guid.TryParse(single, out parsed))
                {
                    guids.Add(parsed);
                }
                return;
            }

            string[] multiple = raw as string[];
            if (multiple == null)
            {
                return;
            }
            foreach (string value in multiple)
            {
                Guid parsed;
                if (Guid.TryParse(value, out parsed))
                {
                    guids.Add(parsed);
                }
            }
        }

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private const uint FileFlagOverlapped = 0x40000000;
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfDeviceInterface = 0x00000010;
        private const int UsbdPipeTypeInterrupt = 3;
        private const int ErrorNoMoreItems = 259;
        private const int ErrorIoPending = 997;
        private const int ErrorOperationAborted = 995;
        private const int ErrorInvalidHandle = 6;
        private const uint WaitObject0 = 0;
        private const uint WaitTimeout = 258;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        [StructLayout(LayoutKind.Sequential)]
        internal struct WINUSB_PIPE_INFORMATION
        {
            public int PipeType;
            public byte PipeId;
            public ushort MaximumPacketSize;
            public byte Interval;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct USB_INTERFACE_DESCRIPTOR
        {
            public byte Length;
            public byte DescriptorType;
            public byte InterfaceNumber;
            public byte AlternateSetting;
            public byte NumberOfEndpoints;
            public byte InterfaceClass;
            public byte InterfaceSubClass;
            public byte InterfaceProtocol;
            public byte Interface;
        }

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
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public uint Offset;
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

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr handle, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Initialize(IntPtr file, out IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_Free(IntPtr interfaceHandle);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateSettingNumber, out USB_INTERFACE_DESCRIPTOR descriptor);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateSettingNumber, byte pipeIndex, out WINUSB_PIPE_INFORMATION pipeInformation);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, IntPtr buffer, uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

        [DllImport("winusb.dll", SetLastError = true)]
        private static extern bool WinUsb_GetOverlappedResult(IntPtr interfaceHandle, IntPtr overlapped, out uint lengthTransferred, bool wait);
    }
}
