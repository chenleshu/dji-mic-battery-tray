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
[assembly: AssemblyVersion("1.3.0.0")]
[assembly: AssemblyFileVersion("1.3.0.0")]

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
        private readonly System.Windows.Forms.Timer timer;
        private readonly string executablePath;
        private readonly string dataRoot;
        private readonly string statusPath;
        private readonly string logPath;
        private bool updating;

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

            statusItem = new ToolStripMenuItem("正在读取…");
            statusItem.Enabled = false;

            detailsItem = new ToolStripMenuItem("设备详情");
            detailsItem.Enabled = false;

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
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(refreshItem);
            menu.Items.Add(autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);

            notifyIcon = new NotifyIcon();
            notifyIcon.ContextMenuStrip = menu;
            notifyIcon.Text = ProductNameZh + "：正在读取";
            notifyIcon.Icon = IconFactory.Create("offline", 0.0);
            notifyIcon.Visible = true;

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 8000;
            timer.Tick += delegate { UpdateStatus(); };

            UpdateStatus();
            timer.Start();
        }

        private void UpdateStatus()
        {
            if (updating)
            {
                return;
            }

            updating = true;
            try
            {
                MicStatusSnapshot snapshot = MicStatusReader.Read(3500);
                TrayView view = TrayView.FromSnapshot(snapshot);
                ApplyView(view);
                WriteStatus(snapshot, view);
            }
            catch (Exception ex)
            {
                notifyIcon.Text = ProductNameZh + "：电量读取失败";
                statusItem.Text = "电量读取失败";
                WriteLog(ex.ToString());
            }
            finally
            {
                updating = false;
            }
        }

        private void ApplyView(TrayView view)
        {
            Icon oldIcon = notifyIcon.Icon;
            notifyIcon.Icon = IconFactory.Create(view.Tone, view.Fill);
            if (oldIcon != null)
            {
                oldIcon.Dispose();
            }

            notifyIcon.Text = LimitTooltip(view.Tooltip);
            statusItem.Text = view.Summary;
            detailsItem.DropDownItems.Clear();
            foreach (string line in view.DetailLines)
            {
                ToolStripMenuItem item = new ToolStripMenuItem(line);
                item.Enabled = false;
                detailsItem.DropDownItems.Add(item);
            }
            detailsItem.Enabled = detailsItem.DropDownItems.Count > 0;
        }

        private static string LimitTooltip(string text)
        {
            return text.Length <= 120 ? text : text.Substring(0, 120);
        }

        private void WriteStatus(MicStatusSnapshot snapshot, TrayView view)
        {
            List<string> lines = new List<string>();
            lines.Add("应用=" + ProductNameZh);
            lines.Add("状态=" + (snapshot.Microphones.Count > 0 ? "ok" : "no_device"));
            lines.Add("麦克风数=" + snapshot.Microphones.Count);
            lines.Add("摘要=" + view.Summary);
            lines.Add("提示=" + view.Tooltip);
            for (int i = 0; i < snapshot.Microphones.Count; i++)
            {
                MicrophoneStatus mic = snapshot.Microphones[i];
                lines.Add(string.Format(
                    "麦克风{0}=标签:{1};来源:{2};设备:{3};电量:{4};档位:{5};充电:{6}",
                    i + 1,
                    mic.Label,
                    mic.Source,
                    mic.DeviceName,
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
            timer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                if (notifyIcon != null)
                {
                    notifyIcon.Visible = false;
                    notifyIcon.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class TrayView
    {
        public string Tone { get; private set; }
        public double Fill { get; private set; }
        public string Tooltip { get; private set; }
        public string Summary { get; private set; }
        public List<string> DetailLines { get; private set; }

        public static TrayView FromSnapshot(MicStatusSnapshot snapshot)
        {
            List<MicrophoneStatus> known = snapshot.Microphones
                .Where(mic => mic.BatteryPercent.HasValue)
                .OrderBy(mic => mic.BatteryPercent.Value)
                .ToList();
            List<string> details = snapshot.Microphones
                .Select(DetailLine)
                .Concat(snapshot.Notices)
                .ToList();

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
                    details
                );
            }

            int minimum = known[0].BatteryPercent.Value;
            bool approximateMinimum = known
                .Where(mic => mic.BatteryPercent.Value == minimum)
                .Any(mic => mic.Approximate);
            BatteryVisual visual = BatteryVisual.FromPercent(minimum);
            string minimumText = (approximateMinimum ? "约 " : "") + minimum + "%";
            string summary = "最低" + minimumText + " · " + snapshot.Microphones.Count + " 支麦克风";
            string tooltip = BuildTooltip(snapshot.Microphones, summary);
            return New(
                visual.Tone,
                visual.Fill,
                tooltip,
                summary,
                details
            );
        }

        private static string BuildTooltip(List<MicrophoneStatus> microphones, string summary)
        {
            List<string> parts = new List<string> { ProductNameZh };
            foreach (MicrophoneStatus mic in microphones)
            {
                string candidate = string.Join(" | ", parts.Concat(new[] { CompactLine(mic) }).ToArray());
                if (candidate.Length > 116)
                {
                    parts.Add(summary + "，右键查看全部");
                    break;
                }
                parts.Add(CompactLine(mic));
            }
            return string.Join(" | ", parts.ToArray());
        }

        private const string ProductNameZh = "大疆麦克风电量";

        private static string CompactLine(MicrophoneStatus mic)
        {
            string battery = mic.BatteryPercent.HasValue
                ? (mic.Approximate ? "约" : "") + mic.BatteryPercent.Value + "%"
                : "未知";
            return mic.Label + " " + battery + (mic.Charging ? " ⚡" : "");
        }

        private static string DetailLine(MicrophoneStatus mic)
        {
            string sourceName = mic.Source == "蓝牙" && !string.IsNullOrWhiteSpace(mic.DeviceName)
                ? mic.DeviceName
                : mic.Label;
            string battery = mic.BatteryPercent.HasValue
                ? (mic.Approximate ? "约 " : "") + mic.BatteryPercent.Value + "%"
                : "电量未知";
            return sourceName + " · " + battery + (mic.Charging ? " · 充电中" : "");
        }

        private static TrayView New(string tone, double fill, string tooltip, string summary, List<string> detailLines)
        {
            return new TrayView {
                Tone = tone,
                Fill = fill,
                Tooltip = tooltip,
                Summary = summary,
                DetailLines = detailLines
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

        public static Icon Create(string tone, double fill)
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

        private static Color ToneColor(string tone)
        {
            if (tone == "good") return Color.FromArgb(54, 201, 110);
            if (tone == "caution") return Color.FromArgb(245, 166, 35);
            if (tone == "critical") return Color.FromArgb(220, 38, 38);
            return Color.FromArgb(145, 150, 160);
        }
    }
}
