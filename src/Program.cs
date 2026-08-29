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
[assembly: AssemblyDescription("在 Windows 通知区域显示 DJI Mic Mini 发射器电量")]
[assembly: AssemblyCompany("chenleshu")]
[assembly: AssemblyProduct("大疆麦克风电量")]
[assembly: AssemblyCopyright("Released under the Unlicense")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

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
                ReaderResult result = Reader.Read(3500);
                TrayView view = TrayView.FromResult(result);
                Icon oldIcon = notifyIcon.Icon;
                notifyIcon.Icon = IconFactory.Create(view.Tone, view.Fill);
                if (oldIcon != null)
                {
                    oldIcon.Dispose();
                }

                notifyIcon.Text = LimitTooltip(view.Tooltip);
                statusItem.Text = view.Summary;
                WriteStatus(result, view);
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

        private static string LimitTooltip(string text)
        {
            return text.Length <= 120 ? text : text.Substring(0, 120);
        }

        private void WriteStatus(ReaderResult result, TrayView view)
        {
            List<string> lines = new List<string>();
            lines.Add("应用=" + ProductNameZh);
            lines.Add("状态=" + result.Status);
            lines.Add("摘要=" + view.Summary);
            lines.Add("提示=" + view.Tooltip);
            lines.Add("协议=" + (result.ProtocolVersion.HasValue ? result.ProtocolVersion.Value.ToString() : ""));
            foreach (TransmitterState tx in result.Transmitters.OrderBy(item => item.Slot))
            {
                lines.Add(string.Format(
                    "TX{0}=连接:{1};档位:{2};充电:{3}",
                    tx.Slot,
                    tx.Connected,
                    tx.BatteryGauge.HasValue ? tx.BatteryGauge.Value.ToString() : "",
                    tx.Charging
                ));
            }
            lines.Add("更新时间=" + DateTime.Now.ToString("o"));
            File.WriteAllLines(statusPath, lines.ToArray(), new UTF8Encoding(false));
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

        public static TrayView FromResult(ReaderResult result)
        {
            if (result.Status == "ok")
            {
                List<TransmitterState> connected = result.Transmitters
                    .Where(tx => tx.Connected)
                    .OrderBy(tx => tx.Slot)
                    .ToList();
                if (connected.Count == 0)
                {
                    return New("offline", 0.0, "大疆麦克风电量：接收器在线，发射器未连接", "接收器在线 · 发射器未连接");
                }

                List<string> parts = new List<string>();
                foreach (TransmitterState tx in connected)
                {
                    GaugeInfo info = GaugeInfo.FromGauge(tx.BatteryGauge);
                    parts.Add("TX" + tx.Slot + " " + info.Label + (tx.Charging ? " ⚡" : ""));
                }

                List<TransmitterState> known = connected
                    .Where(tx => tx.BatteryGauge.HasValue && tx.BatteryGauge.Value >= 1 && tx.BatteryGauge.Value <= 7)
                    .ToList();
                GaugeInfo worst = known.Count == 0
                    ? GaugeInfo.FromGauge(null)
                    : GaugeInfo.FromGauge(known.Max(tx => tx.BatteryGauge.Value));
                string summary = string.Join(" · ", parts.ToArray());
                return New(worst.Tone, worst.Fill, "大疆麦克风电量 | " + string.Join(" | ", parts.ToArray()), summary);
            }

            string fallback;
            if (result.Status == "setup_required") fallback = "需为 Interface 6 安装 WinUSB";
            else if (result.Status == "unsupported_firmware") fallback = "当前固件暂不提供 USB 电量";
            else if (result.Status == "no_data") fallback = "等待大疆麦克风状态数据";
            else fallback = "大疆麦克风电量读取失败";
            return New(result.Status == "setup_required" ? "caution" : "offline", 0.0, ProductNameZh + "：" + fallback, fallback);
        }

        private const string ProductNameZh = "大疆麦克风电量";

        private static TrayView New(string tone, double fill, string tooltip, string summary)
        {
            return new TrayView { Tone = tone, Fill = fill, Tooltip = tooltip, Summary = summary };
        }
    }

    internal sealed class GaugeInfo
    {
        public string Label { get; private set; }
        public string Tone { get; private set; }
        public double Fill { get; private set; }

        public static GaugeInfo FromGauge(int? gauge)
        {
            if (!gauge.HasValue || gauge.Value < 1 || gauge.Value > 7) return New("待采样", "offline", 0.0);
            if (gauge.Value == 1) return New("满电", "good", 1.0);
            if (gauge.Value <= 4) return New("良好", "good", Math.Max(0.25, 1.0 - ((gauge.Value - 1) * 0.25)));
            if (gauge.Value == 5) return New("电量低", "caution", 0.25);
            if (gauge.Value == 6) return New("电量很低", "warning", 0.0);
            return New("极低", "critical", 0.0);
        }

        private static GaugeInfo New(string label, string tone, double fill)
        {
            return new GaugeInfo { Label = label, Tone = tone, Fill = fill };
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
                    else if (tone == "warning" || tone == "critical")
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
            if (tone == "warning") return Color.FromArgb(235, 75, 75);
            if (tone == "critical") return Color.FromArgb(220, 38, 38);
            return Color.FromArgb(145, 150, 160);
        }
    }
}
