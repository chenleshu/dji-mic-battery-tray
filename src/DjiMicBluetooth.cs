using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace DjiMicBattery
{
    public sealed class BluetoothBatteryResult
    {
        public string Status { get; set; }
        public string DeviceName { get; set; }
        public int? BatteryPercent { get; set; }
        public string InstanceId { get; set; }
        public string ProductName { get; set; }
        public string SerialNumber { get; set; }

        public BluetoothBatteryResult()
        {
            Status = "no_device";
            DeviceName = "";
            InstanceId = "";
            ProductName = "";
            SerialNumber = "";
        }
    }

    public static class BluetoothBatteryReader
    {
        private const uint DigcfPresent = 0x00000002;
        private const uint DigcfAllClasses = 0x00000004;
        private const uint SpdrpFriendlyName = 0x0000000C;
        private const string CaptureEndpointsPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture";
        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);
        private static readonly Guid BatteryPropertySet = new Guid("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        public static List<BluetoothBatteryResult> ReadAll()
        {
            IntPtr deviceInfoSet = SetupDiGetClassDevs(
                IntPtr.Zero,
                "BTHENUM",
                IntPtr.Zero,
                DigcfPresent | DigcfAllClasses
            );
            if (deviceInfoSet == InvalidHandleValue)
            {
                return new List<BluetoothBatteryResult> { New("reader_error", "", null, "") };
            }

            try
            {
                List<string> activeCaptureNames = ReadActiveDjiCaptureNames();
                List<BluetoothBatteryResult> results = new List<BluetoothBatteryResult>();
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
                    if (!IsActiveCapture(name, activeCaptureNames))
                    {
                        continue;
                    }

                    int? battery = ReadBatteryPercent(deviceInfoSet, ref deviceInfo);
                    results.Add(New(battery.HasValue ? "ok" : "no_battery", name, battery, instanceId));
                }

                return results
                    .GroupBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.OrderByDescending(item => item.BatteryPercent.HasValue).First())
                    .OrderBy(item => item.DeviceName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.InstanceId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }
        }

        private static List<string> ReadActiveDjiCaptureNames()
        {
            List<string> names = new List<string>();
            using (RegistryKey root = Registry.LocalMachine.OpenSubKey(CaptureEndpointsPath))
            {
                if (root == null)
                {
                    return names;
                }
                foreach (string endpointId in root.GetSubKeyNames())
                {
                    using (RegistryKey endpoint = root.OpenSubKey(endpointId))
                    {
                        object rawState = endpoint == null ? null : endpoint.GetValue("DeviceState");
                        if (rawState == null || Convert.ToInt32(rawState) != 1)
                        {
                            continue;
                        }
                    }
                    using (RegistryKey properties = root.OpenSubKey(endpointId + @"\Properties"))
                    {
                        if (properties == null)
                        {
                            continue;
                        }
                        foreach (string propertyName in properties.GetValueNames())
                        {
                            string value = properties.GetValue(propertyName) as string;
                            if (!string.IsNullOrWhiteSpace(value) &&
                                value.IndexOf("DJI Mic", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                names.Add(value);
                            }
                        }
                    }
                }
            }
            return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static bool IsActiveCapture(string deviceName, List<string> activeCaptureNames)
        {
            string normalizedDevice = NormalizeName(deviceName);
            return activeCaptureNames.Any(name =>
            {
                string normalizedActive = NormalizeName(name);
                return normalizedActive.IndexOf(normalizedDevice, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    normalizedDevice.IndexOf(normalizedActive, StringComparison.OrdinalIgnoreCase) >= 0;
            });
        }

        private static string NormalizeName(string value)
        {
            string normalized = (value ?? "").Trim();
            if (normalized.EndsWith(" AG", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 3).Trim();
            }
            if (normalized.EndsWith(" Hands-Free", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized.Substring(0, normalized.Length - 11).Trim();
            }
            return normalized;
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
            string productName;
            string serialNumber;
            ReadIdentity(name, out productName, out serialNumber);
            return new BluetoothBatteryResult {
                Status = status,
                DeviceName = name,
                BatteryPercent = percent,
                InstanceId = instanceId,
                ProductName = productName,
                SerialNumber = serialNumber
            };
        }

        private static void ReadIdentity(string deviceName, out string productName, out string serialNumber)
        {
            string normalized = NormalizeName(deviceName);
            int separator = normalized.LastIndexOf('-');
            if (separator > 0 && separator < normalized.Length - 1)
            {
                string candidate = normalized.Substring(separator + 1).Trim();
                if (candidate.Length >= 4 && candidate.Length <= 20 && candidate.IndexOf(' ') < 0)
                {
                    productName = normalized.Substring(0, separator).Trim();
                    serialNumber = candidate;
                    return;
                }
            }
            productName = normalized;
            serialNumber = "";
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
