using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DjiMicBattery
{
    public sealed class BluetoothBatteryResult
    {
        public string Status { get; set; }
        public string DeviceName { get; set; }
        public int? BatteryPercent { get; set; }
        public string InstanceId { get; set; }

        public BluetoothBatteryResult()
        {
            Status = "no_device";
            DeviceName = "";
            InstanceId = "";
        }
    }

    public static class BluetoothBatteryReader
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfAllClasses = 0x00000004;
        private const uint SpdrpFriendlyName = 0x0000000C;
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static readonly Guid BatteryPropertySet = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        public static BluetoothBatteryResult Read()
        {
            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                IntPtr.Zero,
                "BTHENUM",
                IntPtr.Zero,
                DigcfPresent | DigcfAllClasses
            );
            if (deviceInfoSet == InvalidHandleValue)
            {
                return New("reader_error", "", null, "");
            }

            try
            {
                BluetoothBatteryResult candidate = null;
                uint index = 0;
                while (true)
                {
                    SP_DEVINFO_DATA deviceInfo = new SP_DEVINFO_DATA();
                    deviceInfo.cbSize = (uint)Marshal.SizeOf(typeof(SP_DEVINFO_DATA));
                    if (!SetupDiEnumDeviceInfo(deviceInfoSet, index++, ref deviceInfo))
                    {
                        break;
                    }

                    string instanceId = ReadInstanceId(deviceInfoSet, ref deviceInfo);
                    if (instanceId.IndexOf("{0000111E-0000-1000-8000-00805F9B34FB}", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    string name = ReadFriendlyName(deviceInfoSet, ref deviceInfo);
                    if (name.IndexOf("DJI Mic", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    int? battery = ReadBatteryPercent(deviceInfoSet, ref deviceInfo);
                    candidate = New(battery.HasValue ? "ok" : "no_battery", name, battery, instanceId);
                    if (battery.HasValue)
                    {
                        return candidate;
                    }
                }

                return candidate ?? New("no_device", "", null, "");
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static int? ReadBatteryPercent(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfo)
        {
            DEVPROPKEY key = new DEVPROPKEY();
            key.fmtid = BatteryPropertySet;
            key.pid = 2;
            uint propertyType;
            uint requiredSize;
            byte[] value = new byte[1];
            if (!SetupDiGetDeviceProperty(
                deviceInfoSet,
                ref deviceInfo,
                ref key,
                out propertyType,
                value,
                (uint)value.Length,
                out requiredSize,
                0
            ))
            {
                return null;
            }

            int percent = value[0];
            return percent <= 100 ? (int?)percent : null;
        }

        private static string ReadFriendlyName(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfo)
        {
            uint propertyType;
            uint requiredSize;
            StringBuilder value = new StringBuilder(256);
            bool ok = SetupDiGetDeviceRegistryProperty(
                deviceInfoSet,
                ref deviceInfo,
                SpdrpFriendlyName,
                out propertyType,
                value,
                (uint)(value.Capacity * 2),
                out requiredSize
            );
            return ok ? value.ToString() : "DJI Mic 蓝牙麦克风";
        }

        private static string ReadInstanceId(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfo)
        {
            uint requiredSize;
            StringBuilder value = new StringBuilder(512);
            bool ok = SetupDiGetDeviceInstanceId(
                deviceInfoSet,
                ref deviceInfo,
                value,
                (uint)value.Capacity,
                out requiredSize
            );
            return ok ? value.ToString() : "";
        }

        private static BluetoothBatteryResult New(string status, string name, int? percent, string instanceId)
        {
            return new BluetoothBatteryResult {
                Status = status,
                DeviceName = name,
                BatteryPercent = percent,
                InstanceId = instanceId
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVINFO_DATA
        {
            public uint cbSize;
            public Guid ClassGuid;
            public uint DevInst;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVPROPKEY
        {
            public Guid fmtid;
            public uint pid;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(
            IntPtr classGuid,
            string enumerator,
            IntPtr hwndParent,
            uint flags
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInfo(
            IntPtr deviceInfoSet,
            uint memberIndex,
            ref SP_DEVINFO_DATA deviceInfoData
        );

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInstanceId(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            StringBuilder deviceInstanceId,
            uint deviceInstanceIdSize,
            out uint requiredSize
        );

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceRegistryProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            uint property,
            out uint propertyRegDataType,
            StringBuilder propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize
        );

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceProperty(
            IntPtr deviceInfoSet,
            ref SP_DEVINFO_DATA deviceInfoData,
            ref DEVPROPKEY propertyKey,
            out uint propertyType,
            byte[] propertyBuffer,
            uint propertyBufferSize,
            out uint requiredSize,
            uint flags
        );

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);
    }
}
