using System;
using System.Collections.Generic;
using System.Linq;

namespace DjiMicBattery
{
    public sealed class MicrophoneStatus
    {
        public string Source { get; set; }
        public string Label { get; set; }
        public string DeviceName { get; set; }
        public string DeviceId { get; set; }
        public int? BatteryPercent { get; set; }
        public bool Approximate { get; set; }
        public bool Charging { get; set; }
        public int? BatteryGauge { get; set; }
        public int? ProtocolVersion { get; set; }
        public string ProductType { get; set; }
        public string SerialNumber { get; set; }
        public string ReceiverSerial { get; set; }
        public string ReceiverProductType { get; set; }

        public MicrophoneStatus()
        {
            Source = "";
            Label = "";
            DeviceName = "";
            DeviceId = "";
            ProductType = "";
            SerialNumber = "";
            ReceiverSerial = "";
            ReceiverProductType = "";
        }
    }

    public sealed class MicStatusSnapshot
    {
        public List<MicrophoneStatus> Microphones { get; set; }
        public List<string> Notices { get; set; }
        public string SampledAt { get; set; }

        public MicStatusSnapshot()
        {
            Microphones = new List<MicrophoneStatus>();
            Notices = new List<string>();
            SampledAt = DateTime.Now.ToString("o");
        }
    }

    public static class MicStatusReader
    {
        public static MicStatusSnapshot Read(int timeoutMilliseconds)
        {
            MicStatusSnapshot snapshot = new MicStatusSnapshot();
            List<BluetoothBatteryResult> bluetooth = BluetoothBatteryReader.ReadAll();
            List<ReaderResult> usb = Reader.ReadAll(timeoutMilliseconds);

            List<BluetoothBatteryResult> activeBluetooth = bluetooth
                .Where(item => item.Status == "ok" || item.Status == "no_battery")
                .ToList();
            for (int i = 0; i < activeBluetooth.Count; i++)
            {
                BluetoothBatteryResult item = activeBluetooth[i];
                snapshot.Microphones.Add(new MicrophoneStatus {
                    Source = "Bluetooth",
                    Label = activeBluetooth.Count == 1 ? "蓝牙" : "蓝牙" + (i + 1),
                    DeviceName = item.DeviceName,
                    DeviceId = item.InstanceId,
                    BatteryPercent = item.BatteryPercent,
                    Approximate = false,
                    ProductType = item.ProductName,
                    SerialNumber = item.SerialNumber
                });
            }

            List<ReaderResult> usbDevices = usb.Where(item => !string.IsNullOrWhiteSpace(item.DeviceId)).ToList();
            for (int receiverIndex = 0; receiverIndex < usbDevices.Count; receiverIndex++)
            {
                ReaderResult receiver = usbDevices[receiverIndex];
                string receiverLabel = "USB" + (receiverIndex + 1);
                if (receiver.Status == "ok")
                {
                    List<TransmitterState> connected = receiver.Transmitters
                        .Where(tx => tx.Connected)
                        .OrderBy(tx => tx.Slot)
                        .ToList();
                    if (connected.Count == 0)
                    {
                        snapshot.Notices.Add(receiverLabel + " 接收器在线，发射器未连接");
                    }
                    foreach (TransmitterState tx in connected)
                    {
                        GaugeInfo gauge = GaugeInfo.FromGauge(tx.BatteryGauge);
                        snapshot.Microphones.Add(new MicrophoneStatus {
                            Source = "USB",
                            Label = receiverLabel + "/TX" + tx.Slot,
                            DeviceName = receiverLabel + " 接收器",
                            DeviceId = receiver.DeviceId,
                            BatteryPercent = gauge.EstimatedPercent,
                            Approximate = true,
                            Charging = tx.Charging,
                            BatteryGauge = tx.BatteryGauge,
                            ProtocolVersion = receiver.ProtocolVersion,
                            ProductType = tx.ProductName,
                            SerialNumber = tx.SerialNumber,
                            ReceiverSerial = receiver.ReceiverSerial,
                            ReceiverProductType = receiver.ReceiverProductName
                        });
                    }
                }
                else
                {
                    snapshot.Notices.Add(receiverLabel + " " + NoticeForUsb(receiver));
                }
            }

            if (snapshot.Microphones.Count == 0 && snapshot.Notices.Count == 0)
            {
                ReaderResult discovery = usb.FirstOrDefault();
                if (discovery != null && discovery.Status == "setup_required")
                {
                    snapshot.Notices.Add("需为 USB Interface 6 安装 WinUSB");
                }
                else
                {
                    snapshot.Notices.Add("未检测到已连接的大疆麦克风");
                }
            }

            return snapshot;
        }

        private static string NoticeForUsb(ReaderResult result)
        {
            if (result.Status == "unsupported_firmware") return "当前固件暂不提供 USB 电量";
            if (result.Status == "no_data") return "等待麦克风状态数据";
            if (result.Status == "setup_required") return "需要安装 WinUSB";
            return "读取失败：" + result.Message;
        }
    }
}
