using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("大疆麦克风电量")]
[assembly: AssemblyDescription("在 Windows 通知区域聚合显示一个或多个 DJI 麦克风的 USB 与蓝牙电量")]
[assembly: AssemblyCompany("chenleshu")]
[assembly: AssemblyProduct("大疆麦克风电量")]
[assembly: AssemblyCopyright("Released under the Unlicense")]
[assembly: AssemblyVersion("2.0.0.0")]
[assembly: AssemblyFileVersion("2.0.0.0")]

namespace DjiMicBattery
{
    internal static class Program
    {
        private const string MutexName = @"Local\DjiMicBatteryTrayZhCn";

        [STAThread]
        private static void Main()
        {
            bool created;
            using (Mutex mutex = new Mutex(true, MutexName, out created))
            {
                if (!created)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApplicationContext());
            }
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private const string ProductNameZh = "大疆麦克风电量";
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem detailsItem;
        private readonly ToolStripMenuItem autostartItem;
        private readonly ToolStripMenuItem buttonMappingItem;
        private readonly ToolStripMenuItem buttonMappingEnabledItem;
        private readonly ToolStripMenuItem autoEnterEnabledItem;
        private readonly ToolStripMenuItem autoEnterSettingsItem;
        private readonly ToolStripMenuItem[] buttonMappingPresets;
        private readonly DjiButtonRemapper buttonRemapper;
        private readonly System.Windows.Forms.Timer timer;
        private readonly Control dispatcher;
        private readonly string executablePath;
        private readonly string dataRoot;
        private readonly string statusPath;
        private readonly string logPath;
        private bool updating;
        private bool shuttingDown;

        public TrayApplicationContext()
        {
            executablePath = Assembly.GetExecutingAssembly().Location;
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                ProductNameZh
            );
            Directory.CreateDirectory(dataRoot);
            statusPath = Path.Combine(dataRoot, "status.txt");
            logPath = Path.Combine(dataRoot, "app.log");
            dispatcher = new Control();
            dispatcher.CreateControl();
            buttonRemapper = new DjiButtonRemapper(Path.Combine(dataRoot, "button-mapping.ini"), dispatcher);

            statusItem = new ToolStripMenuItem("正在读取…");
            statusItem.Enabled = false;

            detailsItem = new ToolStripMenuItem("设备详情");
            detailsItem.Enabled = false;
            detailsItem.DropDown.ImageScalingSize = new Size(68, 26);
            detailsItem.DropDown.MinimumSize = new Size(470, 0);

            buttonMappingItem = new ToolStripMenuItem("连接键短按映射");
            buttonMappingEnabledItem = new ToolStripMenuItem("启用映射");
            buttonMappingEnabledItem.CheckOnClick = true;
            buttonMappingEnabledItem.Click += delegate
            {
                buttonRemapper.SetEnabled(buttonMappingEnabledItem.Checked);
                UpdateButtonMappingMenu();
            };

            buttonMappingPresets = new[] {
                CreateMappingPreset("右 Alt", KeyGesture.RightAlt),
                CreateMappingPreset("右 Alt + Shift", KeyGesture.RightAltShift),
                CreateMappingPreset("右 Alt + 空格", KeyGesture.RightAltSpace)
            };
            ToolStripMenuItem customMappingItem = new ToolStripMenuItem("自定义按键组合…");
            customMappingItem.Click += delegate { ShowCustomMappingDialog(); };
            autoEnterEnabledItem = new ToolStripMenuItem("识别完成后自动回车（右 Alt）");
            autoEnterEnabledItem.CheckOnClick = true;
            autoEnterEnabledItem.Click += delegate
            {
                buttonRemapper.SetAutoEnterSettings(
                    autoEnterEnabledItem.Checked,
                    buttonRemapper.AutoEnterTimeoutMilliseconds,
                    buttonRemapper.AutoEnterStableMilliseconds
                );
                UpdateButtonMappingMenu();
            };
            autoEnterSettingsItem = new ToolStripMenuItem("自动回车设置…");
            autoEnterSettingsItem.Click += delegate { ShowAutoEnterSettingsDialog(); };
            ToolStripMenuItem mappingNoticeItem = new ToolStripMenuItem("WinUSB 拦截大疆按键；Windows 不再收到音量键");
            mappingNoticeItem.Enabled = false;
            buttonMappingItem.DropDownItems.Add(buttonMappingEnabledItem);
            buttonMappingItem.DropDownItems.Add(new ToolStripSeparator());
            buttonMappingItem.DropDownItems.AddRange(buttonMappingPresets);
            buttonMappingItem.DropDownItems.Add(new ToolStripSeparator());
            buttonMappingItem.DropDownItems.Add(customMappingItem);
            buttonMappingItem.DropDownItems.Add(new ToolStripSeparator());
            buttonMappingItem.DropDownItems.Add(autoEnterEnabledItem);
            buttonMappingItem.DropDownItems.Add(autoEnterSettingsItem);
            buttonMappingItem.DropDownItems.Add(mappingNoticeItem);
            buttonMappingItem.DropDownOpening += delegate { UpdateButtonMappingMenu(); };
            UpdateButtonMappingMenu();

            ToolStripMenuItem titleItem = new ToolStripMenuItem(ProductNameZh);
            titleItem.Enabled = false;

            ToolStripMenuItem refreshItem = new ToolStripMenuItem("立即刷新");
            refreshItem.Click += delegate { UpdateStatus(); };

            autostartItem = new ToolStripMenuItem("开机自动启动");
            autostartItem.CheckOnClick = true;
            autostartItem.Checked = IsAutostartEnabled();
            autostartItem.Click += delegate
            {
                try
                {
                    SetAutostart(autostartItem.Checked);
                }
                catch (Exception ex)
                {
                    autostartItem.Checked = IsAutostartEnabled();
                    WriteLog(ex.ToString());
                }
            };

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApplication(); };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add(titleItem);
            menu.Items.Add(statusItem);
            menu.Items.Add(detailsItem);
            menu.Items.Add(buttonMappingItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refreshItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Text = ProductNameZh + "：正在读取";
            notifyIcon.Icon = IconFactory.Create("offline", 0.0, false);
            notifyIcon.Visible = true;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 8000;
            timer.Tick += delegate { UpdateStatus(); };

            UpdateStatus();
            timer.Start();
        }

        private void UpdateStatus()
        {
            if (updating || shuttingDown)
            {
                return;
            }

            updating = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                MicStatusSnapshot snapshot = null;
                TrayView view = null;
                Exception failure = null;
                try
                {
                    snapshot = MicStatusReader.Read(3500);
                    view = TrayView.FromSnapshot(snapshot);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (shuttingDown)
                        {
                            return;
                        }
                        try
                        {
                            if (failure == null)
                            {
                                ApplyView(view);
                                WriteStatus(snapshot, view);
                            }
                            else
                            {
                                notifyIcon.Text = ProductNameZh + "：电量读取失败";
                                statusItem.Text = "电量读取失败";
                                WriteLog(failure.ToString());
                            }
                        }
                        finally
                        {
                            updating = false;
                        }
                    }));
                }
                catch (InvalidOperationException)
                {
                    updating = false;
                }
            });
        }

        private void ApplyView(TrayView view)
        {
            Icon oldIcon = notifyIcon.Icon;
            notifyIcon.Icon = IconFactory.Create(view.Tone, view.Fill, view.Charging);
            if (oldIcon != null)
            {
                oldIcon.Dispose();
            }

            notifyIcon.Text = LimitTooltip(view.Tooltip);
            statusItem.Text = view.Summary;
            if (detailsItem.DropDown.Visible)
            {
                return;
            }
            DisposeDetailItems();
            for (int groupIndex = 0; groupIndex < view.DetailGroups.Count; groupIndex++)
            {
                TrayDetailGroup group = view.DetailGroups[groupIndex];
                if (groupIndex > 0)
                {
                    detailsItem.DropDownItems.Add(new ToolStripSeparator());
                }
                ToolStripMenuItem header = new ToolStripMenuItem(group.Title);
                header.Enabled = false;
                header.Font = new Font(SystemFonts.MenuFont, FontStyle.Bold);
                header.Image = ConnectionIconFactory.Create(group.Kind);
                header.ImageScaling = ToolStripItemImageScaling.None;
                detailsItem.DropDownItems.Add(header);
                foreach (TrayDetailRow row in group.Rows)
                {
                    ToolStripMenuItem item = new ToolStripMenuItem(row.Text);
                    item.Image = BatteryBadgeFactory.Create(row.BatteryPercent, row.Charging);
                    item.ImageScaling = ToolStripItemImageScaling.None;
                    detailsItem.DropDownItems.Add(item);
                }
            }
            detailsItem.Enabled = detailsItem.DropDownItems.Count > 0;
        }

        private void DisposeDetailItems()
        {
            while (detailsItem.DropDownItems.Count > 0)
            {
                ToolStripItem item = detailsItem.DropDownItems[0];
                detailsItem.DropDownItems.RemoveAt(0);
                if (item.Image != null)
                {
                    item.Image.Dispose();
                    item.Image = null;
                }
                item.Dispose();
            }
        }

        private ToolStripMenuItem CreateMappingPreset(string text, KeyGesture gesture)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Tag = gesture;
            item.Click += delegate
            {
                buttonRemapper.SetGesture(gesture);
                buttonRemapper.SetEnabled(true);
                UpdateButtonMappingMenu();
            };
            return item;
        }

        private void UpdateButtonMappingMenu()
        {
            buttonMappingEnabledItem.Checked = buttonRemapper.Enabled;
            autoEnterEnabledItem.Checked = buttonRemapper.AutoEnterEnabled;
            autoEnterSettingsItem.Text = string.Format(
                "自动回车设置…（最长 {0}，补全后 {1}）",
                AutoEnterTiming.FormatSeconds(buttonRemapper.AutoEnterTimeoutMilliseconds),
                AutoEnterTiming.FormatSeconds(buttonRemapper.AutoEnterStableMilliseconds)
            );
            for (int i = 0; i < buttonMappingPresets.Length; i++)
            {
                KeyGesture preset = buttonMappingPresets[i].Tag as KeyGesture;
                buttonMappingPresets[i].Checked = buttonRemapper.Gesture.SameAs(preset);
            }
            buttonMappingItem.Text = "连接键短按映射：" + buttonRemapper.Gesture.DisplayName;
            if (!buttonRemapper.DriverReady)
            {
                buttonMappingItem.Text = "连接键短按映射：需要 MI_00 WinUSB";
            }
            else if (!buttonRemapper.Available)
            {
                buttonMappingItem.Text = "连接键短按映射：接口错误 " + buttonRemapper.ErrorCode;
            }
            else if (!buttonRemapper.HasDjiInputDevice)
            {
                buttonMappingItem.Text += "（等待大疆按键设备）";
            }
        }

        private void ShowCustomMappingDialog()
        {
            using (KeyGestureCaptureForm form = new KeyGestureCaptureForm())
            {
                if (form.ShowDialog() == DialogResult.OK && form.Gesture != null)
                {
                    buttonRemapper.SetGesture(form.Gesture);
                    buttonRemapper.SetEnabled(true);
                    UpdateButtonMappingMenu();
                }
            }
        }

        private void ShowAutoEnterSettingsDialog()
        {
            using (AutoEnterSettingsForm form = new AutoEnterSettingsForm(
                buttonRemapper.AutoEnterEnabled,
                buttonRemapper.AutoEnterTimeoutMilliseconds,
                buttonRemapper.AutoEnterStableMilliseconds
            ))
            {
                if (form.ShowDialog() == DialogResult.OK)
                {
                    buttonRemapper.SetAutoEnterSettings(
                        form.AutoEnterEnabled,
                        form.TimeoutMilliseconds,
                        form.StableMilliseconds
                    );
                    UpdateButtonMappingMenu();
                }
            }
        }

        private static string LimitTooltip(string text)
        {
            return text.Length <= 63 ? text : text.Substring(0, 63);
        }

        private void WriteStatus(MicStatusSnapshot snapshot, TrayView view)
        {
            List<string> lines = new List<string>();
            lines.Add("应用=" + ProductNameZh);
            lines.Add("状态=" + (snapshot.Microphones.Count > 0 ? "ok" : "no_device"));
            lines.Add("麦克风数=" + snapshot.Microphones.Count);
            lines.Add("摘要=" + view.Summary);
            lines.Add("提示=" + view.Tooltip.Replace(Environment.NewLine, " | "));
            lines.Add("连接键映射=" + (buttonRemapper.Enabled ? buttonRemapper.Gesture.DisplayName : "关闭"));
            lines.Add("连接键接口=WinUSB MI_00");
            lines.Add("连接键驱动=" + (buttonRemapper.DriverReady ? "ok" : "missing"));
            lines.Add("连接键监听=" + (buttonRemapper.Available ? "ok" : "error:" + buttonRemapper.ErrorCode));
            lines.Add("连接键错误阶段=" + buttonRemapper.LastErrorStage);
            lines.Add("大疆按键设备=" + (buttonRemapper.HasDjiInputDevice ? buttonRemapper.DjiInputDevicePath : "未检测到"));
            lines.Add("大疆按键设备数=" + buttonRemapper.ConnectedDeviceCount);
            lines.Add("大疆按键端点=" + buttonRemapper.EndpointSummary);
            lines.Add("大疆按键输入=" + buttonRemapper.DjiInputCount);
            lines.Add("大疆按键触发=" + buttonRemapper.DjiMappedCount);
            lines.Add("大疆映射失败=" + buttonRemapper.DjiMappingFailureCount);
            lines.Add("大疆按键释放=" + buttonRemapper.DjiReleaseCount);
            lines.Add("系统音量拦截=" + (buttonRemapper.HasDjiInputDevice ? "source_blocked" : "inactive"));
            lines.Add("最近注入接受=" + buttonRemapper.LastSendInputCount + "/" + buttonRemapper.LastSendInputExpectedCount);
            lines.Add("最近注入错误=" + buttonRemapper.LastSendInputError);
            lines.Add("最近USB报告=" + buttonRemapper.LastDjiReport);
            lines.Add("最近映射时间=" + buttonRemapper.LastDjiTriggerAt);
            lines.Add("自动回车=" + (buttonRemapper.AutoEnterEnabled ? "开启" : "关闭"));
            lines.Add("自动回车状态=" + buttonRemapper.AutoEnterState);
            lines.Add("自动回车最长等待=" + AutoEnterTiming.FormatSeconds(buttonRemapper.AutoEnterTimeoutMilliseconds));
            lines.Add("自动回车稳定延迟=" + AutoEnterTiming.FormatSeconds(buttonRemapper.AutoEnterStableMilliseconds));
            lines.Add("自动回车次数=" + buttonRemapper.AutoEnterSubmitCount);
            lines.Add("最近自动回车时间=" + buttonRemapper.LastAutoEnterAt);
            for (int i = 0; i < snapshot.Microphones.Count; i++)
            {
                MicrophoneStatus mic = snapshot.Microphones[i];
                lines.Add(string.Format(
                    "麦克风{0}=标签:{1};来源:{2};产品:{3};识别号:{4};接收器序列:{5};电量:{6};档位:{7};充电:{8}",
                    i + 1,
                    mic.Label,
                    FormatSource(mic.Source),
                    mic.ProductType,
                    mic.SerialNumber,
                    mic.ReceiverSerial,
                    FormatBattery(mic),
                    mic.BatteryGauge.HasValue ? mic.BatteryGauge.Value.ToString() : "",
                    mic.Charging
                ));
            }
            for (int i = 0; i < snapshot.Notices.Count; i++)
            {
                lines.Add("提示" + (i + 1) + "=" + snapshot.Notices[i]);
            }
            lines.Add("更新时间=" + DateTime.Now.ToString("o"));
            File.WriteAllLines(statusPath, lines.ToArray(), new UTF8Encoding(false));
        }

        private static string FormatBattery(MicrophoneStatus mic)
        {
            if (!mic.BatteryPercent.HasValue) return "未知";
            return (mic.Approximate ? "约" : "") + mic.BatteryPercent.Value + "%";
        }

        private static string FormatSource(string source)
        {
            return source == "Bluetooth" ? "蓝牙" : source;
        }

        private void WriteLog(string message)
        {
            File.AppendAllText(
                logPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + message + Environment.NewLine,
                new UTF8Encoding(false)
            );
        }

        private bool IsAutostartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath))
            {
                string value = key == null ? null : key.GetValue(ProductNameZh) as string;
                return string.Equals(value, Quote(executablePath), StringComparison.OrdinalIgnoreCase);
            }
        }

        private void SetAutostart(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (enabled)
                {
                    key.SetValue(ProductNameZh, Quote(executablePath), RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ProductNameZh, false);
                }
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value + "\"";
        }

        private void ExitApplication()
        {
            shuttingDown = true;
            timer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                shuttingDown = true;
                timer.Dispose();
                buttonRemapper.Dispose();
                dispatcher.Dispose();
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayDetailRow
    {
        public string Text { get; set; }
        public int? BatteryPercent { get; set; }
        public bool Charging { get; set; }
    }

    internal sealed class TrayDetailGroup
    {
        public string Kind { get; set; }
        public string Title { get; set; }
        public List<TrayDetailRow> Rows { get; private set; }

        public TrayDetailGroup()
        {
            Kind = "";
            Title = "";
            Rows = new List<TrayDetailRow>();
        }
    }

    internal sealed class TrayView
    {
        public string Tone { get; private set; }
        public double Fill { get; private set; }
        public string Tooltip { get; private set; }
        public string Summary { get; private set; }
        public bool Charging { get; private set; }
        public List<TrayDetailGroup> DetailGroups { get; private set; }

        public static TrayView FromSnapshot(MicStatusSnapshot snapshot)
        {
            List<MicrophoneStatus> known = snapshot.Microphones
                .Where(mic => mic.BatteryPercent.HasValue)
                .OrderBy(mic => mic.BatteryPercent.Value)
                .ToList();
            List<TrayDetailGroup> detailGroups = BuildDetailGroups(snapshot);
            int chargingCount = snapshot.Microphones.Count(mic => mic.Charging);
            bool charging = chargingCount > 0;

            if (known.Count == 0)
            {
                string fallback = snapshot.Microphones.Count > 0
                    ? "已连接 " + snapshot.Microphones.Count + " 支麦克风 · 电量未知"
                    : (snapshot.Notices.FirstOrDefault() ?? "未检测到已连接的大疆麦克风");
                return New(
                    fallback.IndexOf("WinUSB", StringComparison.OrdinalIgnoreCase) >= 0 ? "caution" : "offline",
                    0.0,
                    ProductNameZh + " | " + fallback,
                    fallback,
                    charging,
                    detailGroups
                );
            }

            int minimum = known[0].BatteryPercent.Value;
            BatteryVisual visual = BatteryVisual.FromPercent(minimum);
            string summary = "最低" + minimum + "% · " + snapshot.Microphones.Count + " 支麦克风";
            if (charging)
            {
                summary += " · 充电中 " + chargingCount + " 支";
            }
            string tooltip = BuildTooltip(snapshot.Microphones);
            return New(
                visual.Tone,
                visual.Fill,
                tooltip,
                summary,
                charging,
                detailGroups
            );
        }

        private static string BuildTooltip(List<MicrophoneStatus> microphones)
        {
            List<string> lines = new List<string>();
            IEnumerable<MicrophoneStatus> ordered = microphones
                .OrderBy(mic => mic.Source == "Bluetooth" ? 0 : 1)
                .ThenBy(mic => mic.Label);
            foreach (MicrophoneStatus mic in ordered)
            {
                string candidate = string.Join(Environment.NewLine, lines.Concat(new[] { CompactLine(mic) }).ToArray());
                if (candidate.Length > 63)
                {
                    const string more = "更多设备请右键查看";
                    if (string.Join(Environment.NewLine, lines.Concat(new[] { more }).ToArray()).Length <= 63)
                    {
                        lines.Add(more);
                    }
                    break;
                }
                lines.Add(CompactLine(mic));
            }
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        private const string ProductNameZh = "大疆麦克风电量";

        private static string CompactLine(MicrophoneStatus mic)
        {
            string battery = mic.BatteryPercent.HasValue
                ? "\U0001F50B" + mic.BatteryPercent.Value + "%"
                : "未知";
            if (mic.Charging)
            {
                battery += "\u26A1";
            }
            bool bluetooth = mic.Source == "Bluetooth";
            string symbol = bluetooth ? "\U0001F4F6" : "\U0001F50C";
            string interfaceName = bluetooth ? "蓝牙" : mic.Label.Replace("/TX", "/T");
            return symbol + interfaceName + " " + CompactProductType(mic.ProductType) + battery;
        }

        private static string CompactProductType(string productType)
        {
            string product = ValueOrUnknown(productType);
            const string djiMicPrefix = "DJI Mic ";
            return product.StartsWith(djiMicPrefix, StringComparison.OrdinalIgnoreCase)
                ? product.Substring(djiMicPrefix.Length)
                : product;
        }

        private static List<TrayDetailGroup> BuildDetailGroups(MicStatusSnapshot snapshot)
        {
            List<TrayDetailGroup> groups = new List<TrayDetailGroup>();
            List<MicrophoneStatus> bluetooth = snapshot.Microphones
                .Where(mic => mic.Source == "Bluetooth")
                .OrderBy(mic => mic.SerialNumber)
                .ToList();
            if (bluetooth.Count > 0)
            {
                TrayDetailGroup group = new TrayDetailGroup {
                    Kind = "Bluetooth",
                    Title = "蓝牙连接（" + bluetooth.Count + " 支）"
                };
                foreach (MicrophoneStatus mic in bluetooth)
                {
                    group.Rows.Add(DetailRow(mic));
                }
                groups.Add(group);
            }

            IEnumerable<IGrouping<string, MicrophoneStatus>> usbGroups = snapshot.Microphones
                .Where(mic => mic.Source == "USB")
                .GroupBy(mic => mic.DeviceId)
                .OrderBy(group => group.First().Label);
            foreach (IGrouping<string, MicrophoneStatus> usbGroup in usbGroups)
            {
                List<MicrophoneStatus> microphones = usbGroup.OrderBy(mic => mic.Label).ToList();
                MicrophoneStatus first = microphones[0];
                string receiver = first.Label.Split('/')[0] + " 接收器";
                if (!string.IsNullOrWhiteSpace(first.ReceiverProductType))
                {
                    receiver += " · " + first.ReceiverProductType;
                }
                receiver += " · SN " + ValueOrUnknown(first.ReceiverSerial);
                receiver += "（" + microphones.Count + " 支）";
                TrayDetailGroup group = new TrayDetailGroup { Kind = "USB", Title = receiver };
                foreach (MicrophoneStatus mic in microphones)
                {
                    group.Rows.Add(DetailRow(mic));
                }
                groups.Add(group);
            }

            if (snapshot.Notices.Count > 0)
            {
                TrayDetailGroup notices = new TrayDetailGroup { Title = "状态提示" };
                foreach (string notice in snapshot.Notices)
                {
                    notices.Rows.Add(new TrayDetailRow { Text = notice, BatteryPercent = null, Charging = false });
                }
                groups.Add(notices);
            }
            return groups;
        }

        private static TrayDetailRow DetailRow(MicrophoneStatus mic)
        {
            string role = mic.Source == "Bluetooth" ? "蓝牙麦克风" : mic.Label.Split('/').Last();
            string text = role + " · " + ValueOrUnknown(mic.ProductType) + " · 识别号 " + ValueOrUnknown(mic.SerialNumber);
            if (mic.Charging) text += " · 充电中";
            return new TrayDetailRow {
                Text = text,
                BatteryPercent = mic.BatteryPercent,
                Charging = mic.Charging
            };
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "未识别" : value;
        }

        private static TrayView New(string tone, double fill, string tooltip, string summary, bool charging, List<TrayDetailGroup> detailGroups)
        {
            return new TrayView {
                Tone = tone,
                Fill = fill,
                Tooltip = tooltip,
                Summary = summary,
                Charging = charging,
                DetailGroups = detailGroups
            };
        }
    }

    internal sealed class BatteryVisual
    {
        public string Tone { get; private set; }
        public double Fill { get; private set; }

        public static BatteryVisual FromPercent(int percent)
        {
            int bounded = Math.Max(0, Math.Min(100, percent));
            string tone = bounded <= 5 ? "critical" : bounded < 10 ? "caution" : "good";
            return new BatteryVisual { Tone = tone, Fill = bounded / 100.0 };
        }
    }

    internal sealed class GaugeInfo
    {
        public string Label { get; private set; }
        public string Tone { get; private set; }
        public double Fill { get; private set; }
        public int? EstimatedPercent { get; private set; }

        public static GaugeInfo FromGauge(int? gauge)
        {
            if (!gauge.HasValue || gauge.Value < 1 || gauge.Value > 7) return New("待采样", "offline", 0.0, null);
            if (gauge.Value == 1) return New("满电", "good", 1.00, 100);
            if (gauge.Value == 2) return New("良好", "good", 0.80, 80);
            if (gauge.Value == 3) return New("良好", "good", 0.60, 60);
            if (gauge.Value == 4) return New("良好", "good", 0.40, 40);
            if (gauge.Value == 5) return New("电量低", "good", 0.20, 20);
            if (gauge.Value == 6) return New("电量很低", "caution", 0.09, 9);
            return New("极低", "critical", 0.05, 5);
        }

        private static GaugeInfo New(string label, string tone, double fill, int? estimatedPercent)
        {
            return new GaugeInfo {
                Label = label,
                Tone = tone,
                Fill = fill,
                EstimatedPercent = estimatedPercent
            };
        }
    }

    internal static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create(string tone, double fill, bool charging)
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                Color color = ToneColor(tone);
                using (Pen shadowPen = new Pen(Color.FromArgb(180, 25, 28, 34), 4))
                using (Pen colorPen = new Pen(color, 2))
                using (SolidBrush brush = new SolidBrush(color))
                {
                    graphics.DrawRectangle(shadowPen, 3, 7, 23, 18);
                    graphics.DrawRectangle(colorPen, 4, 8, 21, 16);
                    graphics.FillRectangle(brush, 27, 12, 3, 8);
                    int fillWidth = (int)Math.Round(17 * Math.Max(0, Math.Min(1, fill)));
                    if (fillWidth > 0)
                    {
                        graphics.FillRectangle(brush, 6, 10, fillWidth, 12);
                    }
                    else if (tone == "critical")
                    {
                        using (Font font = new Font("Segoe UI", 12, FontStyle.Bold, GraphicsUnit.Pixel))
                        {
                            graphics.DrawString("!", font, brush, 10, 8);
                        }
                    }
                }

                if (charging)
                {
                    ChargingGlyph.Draw(graphics, new Rectangle(8, 7, 13, 19));
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    return (Icon)Icon.FromHandle(handle).Clone();
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        internal static Color ToneColor(string tone)
        {
            if (tone == "good") return Color.FromArgb(54, 201, 110);
            if (tone == "caution") return Color.FromArgb(245, 166, 35);
            if (tone == "critical") return Color.FromArgb(220, 38, 38);
            return Color.FromArgb(145, 150, 160);
        }
    }

    internal static class BatteryBadgeFactory
    {
        public static Bitmap Create(int? percent, bool charging)
        {
            const int width = 68;
            const int height = 26;
            Bitmap bitmap = new Bitmap(width, height);
            bitmap.SetResolution(96, 96);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.Clear(Color.Transparent);

                int bounded = percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)) : 0;
                string tone = percent.HasValue ? BatteryVisual.FromPercent(bounded).Tone : "offline";
                Color color = IconFactory.ToneColor(tone);
                Rectangle body = new Rectangle(1, 2, 62, 22);
                Rectangle inner = new Rectangle(4, 5, 56, 16);
                using (GraphicsPath bodyPath = RoundedRectangle(body, 4))
                using (GraphicsPath innerPath = RoundedRectangle(inner, 2))
                using (SolidBrush background = new SolidBrush(Color.FromArgb(48, 53, 61)))
                using (SolidBrush fill = new SolidBrush(color))
                using (SolidBrush terminal = new SolidBrush(color))
                using (Pen outline = new Pen(color, 1.6f))
                using (Font font = new Font("Segoe UI", 8.5f, FontStyle.Bold, GraphicsUnit.Point))
                {
                    graphics.FillPath(background, bodyPath);
                    int fillWidth = percent.HasValue ? (int)Math.Round(inner.Width * bounded / 100.0) : 0;
                    if (fillWidth > 0)
                    {
                        Region oldClip = graphics.Clip;
                        graphics.SetClip(innerPath);
                        graphics.FillRectangle(fill, inner.X, inner.Y, fillWidth, inner.Height);
                        graphics.Clip = oldClip;
                        oldClip.Dispose();
                    }
                    graphics.DrawPath(outline, bodyPath);
                    graphics.FillRectangle(terminal, 64, 8, 4, 10);
                    string label = percent.HasValue
                        ? bounded + "%"
                        : "--";
                    TextRenderer.DrawText(
                        graphics,
                        label,
                        font,
                        body,
                        Color.White,
                        TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter |
                        TextFormatFlags.SingleLine |
                        TextFormatFlags.NoPadding
                    );
                    if (charging)
                    {
                        ChargingGlyph.Draw(graphics, new Rectangle(5, 5, 11, 16));
                    }
                }
            }
            return bitmap;
        }

        private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal static class ChargingGlyph
    {
        public static void Draw(Graphics graphics, Rectangle bounds)
        {
            Point[] points = {
                new Point(bounds.Left + bounds.Width * 6 / 10, bounds.Top),
                new Point(bounds.Left + bounds.Width * 2 / 10, bounds.Top + bounds.Height * 6 / 10),
                new Point(bounds.Left + bounds.Width * 5 / 10, bounds.Top + bounds.Height * 6 / 10),
                new Point(bounds.Left + bounds.Width * 3 / 10, bounds.Bottom),
                new Point(bounds.Right, bounds.Top + bounds.Height * 4 / 10),
                new Point(bounds.Left + bounds.Width * 6 / 10, bounds.Top + bounds.Height * 4 / 10)
            };
            using (GraphicsPath path = new GraphicsPath())
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(255, 214, 10)))
            using (Pen outline = new Pen(Color.FromArgb(210, 24, 28, 34), 1.4f))
            {
                path.AddPolygon(points);
                graphics.FillPath(fill, path);
                graphics.DrawPath(outline, path);
            }
        }
    }

    internal static class ConnectionIconFactory
    {
        public static Bitmap Create(string kind)
        {
            if (kind != "Bluetooth" && kind != "USB") return null;

            Bitmap bitmap = new Bitmap(22, 22);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);
                Color background = kind == "Bluetooth"
                    ? Color.FromArgb(24, 119, 242)
                    : Color.FromArgb(86, 94, 108);
                using (SolidBrush circle = new SolidBrush(background))
                using (Pen symbol = new Pen(Color.White, 1.8f))
                {
                    symbol.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                    symbol.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                    symbol.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                    graphics.FillEllipse(circle, 1, 1, 20, 20);
                    if (kind == "Bluetooth")
                    {
                        graphics.DrawLines(symbol, new[] {
                            new PointF(6, 6),
                            new PointF(16, 16),
                            new PointF(10, 20),
                            new PointF(10, 2),
                            new PointF(16, 8),
                            new PointF(6, 18)
                        });
                    }
                    else
                    {
                        graphics.DrawLine(symbol, 11, 18, 11, 5);
                        graphics.DrawLine(symbol, 11, 10, 6, 7);
                        graphics.DrawLine(symbol, 11, 13, 16, 9);
                        using (SolidBrush white = new SolidBrush(Color.White))
                        {
                            graphics.FillPolygon(white, new[] {
                                new PointF(11, 2),
                                new PointF(8.5f, 6),
                                new PointF(13.5f, 6)
                            });
                            graphics.FillEllipse(white, 4, 5, 4, 4);
                            graphics.FillRectangle(white, 15, 7, 4, 4);
                            graphics.FillEllipse(white, 9, 16, 4, 4);
                        }
                    }
                }
            }
            return bitmap;
        }
    }
}
