using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace DjiMicBattery
{
    internal enum DjiButtonReportState
    {
        Ignore,
        Press,
        Release
    }

    internal sealed class KeyGesture
    {
        private readonly int[] virtualKeys;

        public int[] VirtualKeys
        {
            get { return virtualKeys.ToArray(); }
        }

        public string DisplayName
        {
            get { return string.Join(" + ", virtualKeys.Select(DisplayKey).ToArray()); }
        }

        public static KeyGesture RightAlt
        {
            get { return new KeyGesture(new[] { 0xA5 }); }
        }

        public static KeyGesture RightAltShift
        {
            get { return new KeyGesture(new[] { 0xA5, 0xA1 }); }
        }

        public static KeyGesture RightAltSpace
        {
            get { return new KeyGesture(new[] { 0xA5, 0x20 }); }
        }

        public KeyGesture(IEnumerable<int> keys)
        {
            virtualKeys = keys
                .Where(key => key > 0 && key <= 0xFF)
                .Distinct()
                .Take(6)
                .ToArray();
            if (virtualKeys.Length == 0)
            {
                throw new ArgumentException("按键组合不能为空。", "keys");
            }
        }

        public string Serialize()
        {
            return string.Join(",", virtualKeys.Select(key => key.ToString()).ToArray());
        }

        public bool SameAs(KeyGesture other)
        {
            return other != null && virtualKeys.SequenceEqual(other.virtualKeys);
        }

        public static KeyGesture Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return RightAlt;
            }
            List<int> keys = new List<int>();
            foreach (string part in value.Split(','))
            {
                int key;
                if (int.TryParse(part.Trim(), out key) && key > 0 && key <= 0xFF)
                {
                    keys.Add(key);
                }
            }
            return keys.Count == 0 ? RightAlt : new KeyGesture(keys);
        }

        private static string DisplayKey(int virtualKey)
        {
            switch (virtualKey)
            {
                case 0xA0: return "左 Shift";
                case 0xA1: return "右 Shift";
                case 0x10: return "Shift";
                case 0xA2: return "左 Ctrl";
                case 0xA3: return "右 Ctrl";
                case 0x11: return "Ctrl";
                case 0xA4: return "左 Alt";
                case 0xA5: return "右 Alt";
                case 0x12: return "Alt";
                case 0x5B: return "左 Win";
                case 0x5C: return "右 Win";
                case 0x20: return "空格";
                case 0x0D: return "Enter";
                case 0x1B: return "Esc";
                case 0x09: return "Tab";
                case 0x08: return "Backspace";
                case 0xAF: return "音量加";
                case 0xAE: return "音量减";
                case 0xAD: return "静音";
                default:
                    string name = ((Keys)virtualKey).ToString();
                    return name == "None" ? "VK " + virtualKey.ToString("X2") : name;
            }
        }
    }

    internal sealed class DjiButtonRemapper : IDisposable
    {
        private const uint InputKeyboard = 1;
        private const uint KeyEventExtendedKey = 0x0001;
        private const uint KeyEventKeyUp = 0x0002;
        private const int GestureHoldMilliseconds = 120;

        private readonly string configPath;
        private readonly DjiButtonWinUsbSource buttonSource;
        private readonly TypelessAutoEnterController autoEnterController;
        private readonly HashSet<string> djiButtonsDown;
        private readonly List<PendingGestureRelease> pendingGestureReleases;
        private bool enabled;
        private bool autoEnterEnabled;
        private int autoEnterTimeoutMilliseconds;
        private int autoEnterStableMilliseconds;
        private KeyGesture gesture;

        public bool Available
        {
            get { return buttonSource.Available; }
        }

        public bool DriverReady
        {
            get { return buttonSource.DriverReady; }
        }

        public int ConnectedDeviceCount
        {
            get { return buttonSource.ConnectedDeviceCount; }
        }

        public string EndpointSummary
        {
            get { return buttonSource.EndpointSummary; }
        }

        public bool HasDjiInputDevice
        {
            get { return buttonSource.HasDjiDevice; }
        }

        public string DjiInputDevicePath
        {
            get { return buttonSource.DjiDevicePath; }
        }

        public int ErrorCode
        {
            get { return buttonSource.ErrorCode; }
        }

        public string LastErrorStage
        {
            get { return buttonSource.LastErrorStage; }
        }

        public int DjiInputCount { get; private set; }
        public int DjiMappedCount { get; private set; }
        public int DjiMappingFailureCount { get; private set; }
        public int DjiReleaseCount { get; private set; }
        public int LastSendInputCount { get; private set; }
        public int LastSendInputExpectedCount { get; private set; }
        public int LastSendInputError { get; private set; }
        public string LastDjiReport { get; private set; }
        public string LastDjiTriggerAt { get; private set; }

        public bool Enabled
        {
            get { return enabled; }
        }

        public KeyGesture Gesture
        {
            get { return gesture; }
        }

        public bool AutoEnterEnabled
        {
            get { return autoEnterEnabled; }
        }

        public int AutoEnterTimeoutMilliseconds
        {
            get { return autoEnterTimeoutMilliseconds; }
        }

        public int AutoEnterStableMilliseconds
        {
            get { return autoEnterStableMilliseconds; }
        }

        public string AutoEnterState
        {
            get
            {
                if (!autoEnterEnabled) return "关闭";
                if (!gesture.SameAs(KeyGesture.RightAlt)) return "仅在右 Alt 映射下启用";
                return autoEnterController.State;
            }
        }

        public int AutoEnterSubmitCount
        {
            get { return autoEnterController.SubmitCount; }
        }

        public string LastAutoEnterAt
        {
            get { return autoEnterController.LastSubmittedAt; }
        }

        public DjiButtonRemapper(string configPath, Control dispatcher)
        {
            this.configPath = configPath;
            djiButtonsDown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            pendingGestureReleases = new List<PendingGestureRelease>();
            enabled = true;
            autoEnterEnabled = true;
            autoEnterTimeoutMilliseconds = AutoEnterTiming.DefaultTimeoutMilliseconds;
            autoEnterStableMilliseconds = AutoEnterTiming.DefaultStableMilliseconds;
            gesture = KeyGesture.RightAlt;
            LastDjiReport = "";
            LastDjiTriggerAt = "";
            Load();

            buttonSource = new DjiButtonWinUsbSource(dispatcher);
            buttonSource.InputReceived += OnInput;
            buttonSource.DevicesChanged += OnInputDevicesChanged;
            autoEnterController = new TypelessAutoEnterController(dispatcher, SendEnter);
        }

        public void SetEnabled(bool value)
        {
            enabled = value;
            djiButtonsDown.Clear();
            if (!enabled)
            {
                ResetAutoEnter("映射已关闭");
            }
            Save();
        }

        public void SetGesture(KeyGesture value)
        {
            if (value == null)
            {
                throw new ArgumentNullException("value");
            }
            gesture = value;
            ResetAutoEnter("映射已更改");
            Save();
        }

        public void SetAutoEnterSettings(bool value, int timeoutMilliseconds, int stableMilliseconds)
        {
            autoEnterEnabled = value;
            autoEnterTimeoutMilliseconds = AutoEnterTiming.NormalizeTimeout(timeoutMilliseconds);
            autoEnterStableMilliseconds = AutoEnterTiming.NormalizeStable(stableMilliseconds);
            ResetAutoEnter(autoEnterEnabled ? "待命" : "关闭");
            Save();
        }

        private void OnInput(DjiButtonReportState state, string devicePath, string reportHex)
        {
            DjiInputCount++;
            LastDjiReport = reportHex;
            if (state == DjiButtonReportState.Ignore)
            {
                return;
            }

            string deviceKey = string.IsNullOrWhiteSpace(devicePath) ? "<unknown>" : devicePath;
            if (state == DjiButtonReportState.Release)
            {
                if (djiButtonsDown.Remove(deviceKey))
                {
                    DjiReleaseCount++;
                }
                return;
            }

            if (!djiButtonsDown.Add(deviceKey))
            {
                return;
            }

            if (!enabled || !Available)
            {
                return;
            }

            bool autoEnterActive = autoEnterEnabled && gesture.SameAs(KeyGesture.RightAlt);
            bool finishingRecognition = autoEnterActive && autoEnterController.IsRecording;
            if (autoEnterActive && !finishingRecognition)
            {
                autoEnterController.BeginRecording(
                    autoEnterTimeoutMilliseconds,
                    autoEnterStableMilliseconds
                );
            }
            else if (finishingRecognition)
            {
                // Capture the stop baseline before Right Alt tells Typeless to finish.
                // A failure only disables auto Enter for this cycle; the mapping itself
                // must still be delivered so the user can stop recognition normally.
                autoEnterController.PrepareFinishRecording();
            }

            Action<bool> releaseCompleted = null;
            if (autoEnterActive)
            {
                releaseCompleted = delegate(bool released)
                {
                    if (!released)
                    {
                        ResetAutoEnter("快捷键释放失败");
                    }
                    else if (finishingRecognition)
                    {
                        autoEnterController.FinishRecording();
                    }
                };
            }

            if (SendGesture(gesture, releaseCompleted))
            {
                DjiMappedCount++;
                LastDjiTriggerAt = DateTime.Now.ToString("o");
            }
            else
            {
                if (autoEnterActive)
                {
                    ResetAutoEnter("快捷键注入失败");
                }
                DjiMappingFailureCount++;
            }
        }

        private void OnInputDevicesChanged()
        {
            djiButtonsDown.Clear();
            ResetAutoEnter("待命");
        }

        private bool SendGesture(KeyGesture value, Action<bool> releaseCompleted)
        {
            int[] keys = value.VirtualKeys;
            List<KeyboardInput> keyDownInputs = new List<KeyboardInput>();
            foreach (int key in keys)
            {
                keyDownInputs.Add(InputFor(key, false));
            }
            List<KeyboardInput> keyUpInputs = new List<KeyboardInput>();
            for (int i = keys.Length - 1; i >= 0; i--)
            {
                keyUpInputs.Add(InputFor(keys[i], true));
            }
            KeyboardInput[] down = keyDownInputs.ToArray();
            KeyboardInput[] up = keyUpInputs.ToArray();
            int inputSize = Marshal.SizeOf(typeof(KeyboardInput));
            LastSendInputExpectedCount = down.Length + up.Length;
            uint acceptedDown = SendInput((uint)down.Length, down, inputSize);
            int downError = acceptedDown == down.Length ? 0 : Marshal.GetLastWin32Error();
            LastSendInputCount = (int)acceptedDown;
            LastSendInputError = downError;
            if (acceptedDown != down.Length)
            {
                uint cleanupAccepted = SendInput((uint)up.Length, up, inputSize);
                LastSendInputCount += (int)cleanupAccepted;
                return false;
            }

            PendingGestureRelease pending = new PendingGestureRelease();
            pending.Inputs = up;
            pending.ReleaseCompleted = releaseCompleted;
            pending.Timer = new System.Windows.Forms.Timer();
            pending.Timer.Interval = GestureHoldMilliseconds;
            pending.Timer.Tick += delegate { CompleteGestureRelease(pending); };
            pendingGestureReleases.Add(pending);
            pending.Timer.Start();
            return true;
        }

        private void CompleteGestureRelease(PendingGestureRelease pending)
        {
            pending.Timer.Stop();
            uint accepted = SendInput(
                (uint)pending.Inputs.Length,
                pending.Inputs,
                Marshal.SizeOf(typeof(KeyboardInput))
            );
            if (pendingGestureReleases.Remove(pending))
            {
                LastSendInputCount += (int)accepted;
                if (accepted != pending.Inputs.Length)
                {
                    LastSendInputError = Marshal.GetLastWin32Error();
                    DjiMappingFailureCount++;
                }
            }
            if (pending.ReleaseCompleted != null)
            {
                pending.ReleaseCompleted(accepted == pending.Inputs.Length);
            }
            pending.Timer.Dispose();
        }

        private bool SendEnter()
        {
            KeyboardInput[] inputs = new[] {
                InputFor(0x0D, false),
                InputFor(0x0D, true)
            };
            LastSendInputExpectedCount = inputs.Length;
            uint accepted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(KeyboardInput)));
            LastSendInputCount = (int)accepted;
            LastSendInputError = accepted == inputs.Length ? 0 : Marshal.GetLastWin32Error();
            if (accepted != inputs.Length)
            {
                DjiMappingFailureCount++;
            }
            return accepted == inputs.Length;
        }

        private void ResetAutoEnter(string state)
        {
            if (autoEnterController != null)
            {
                autoEnterController.Cancel(state);
            }
        }

        private static KeyboardInput InputFor(int virtualKey, bool keyUp)
        {
            uint flags = IsExtendedKey(virtualKey) ? KeyEventExtendedKey : 0;
            if (keyUp)
            {
                flags |= KeyEventKeyUp;
            }
            KeyboardInput input = new KeyboardInput();
            input.Type = InputKeyboard;
            input.Union.Keyboard = new KeyboardInputData {
                VirtualKey = (ushort)virtualKey,
                ScanCode = (ushort)MapVirtualKey((uint)virtualKey, 0),
                Flags = flags,
                Time = 0,
                ExtraInfo = UIntPtr.Zero
            };
            return input;
        }

        private static bool IsExtendedKey(int key)
        {
            switch (key)
            {
                case 0xA3:
                case 0xA5:
                case 0x2D:
                case 0x2E:
                case 0x24:
                case 0x23:
                case 0x21:
                case 0x22:
                case 0x25:
                case 0x26:
                case 0x27:
                case 0x28:
                case 0x5B:
                case 0x5C:
                case 0x5D:
                case 0xAD:
                case 0xAE:
                case 0xAF:
                    return true;
                default:
                    return false;
            }
        }

        private void Load()
        {
            if (!File.Exists(configPath))
            {
                Save();
                return;
            }
            foreach (string line in File.ReadAllLines(configPath, Encoding.UTF8))
            {
                if (line.StartsWith("enabled=", StringComparison.OrdinalIgnoreCase))
                {
                    enabled = line.Substring("enabled=".Length).Trim() != "0";
                }
                else if (line.StartsWith("keys=", StringComparison.OrdinalIgnoreCase))
                {
                    gesture = KeyGesture.Parse(line.Substring("keys=".Length));
                }
                else if (line.StartsWith("auto_enter_enabled=", StringComparison.OrdinalIgnoreCase))
                {
                    autoEnterEnabled = line.Substring("auto_enter_enabled=".Length).Trim() != "0";
                }
                else if (line.StartsWith("auto_enter_timeout_ms=", StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(line.Substring("auto_enter_timeout_ms=".Length).Trim(), out value))
                    {
                        autoEnterTimeoutMilliseconds = AutoEnterTiming.NormalizeTimeout(value);
                    }
                }
                else if (line.StartsWith("auto_enter_stable_ms=", StringComparison.OrdinalIgnoreCase))
                {
                    int value;
                    if (int.TryParse(line.Substring("auto_enter_stable_ms=".Length).Trim(), out value))
                    {
                        autoEnterStableMilliseconds = AutoEnterTiming.NormalizeStable(value);
                    }
                }
            }
        }

        private void Save()
        {
            string directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllLines(
                configPath,
                new[] {
                    enabled ? "enabled=1" : "enabled=0",
                    "keys=" + gesture.Serialize(),
                    autoEnterEnabled ? "auto_enter_enabled=1" : "auto_enter_enabled=0",
                    "auto_enter_timeout_ms=" + autoEnterTimeoutMilliseconds,
                    "auto_enter_stable_ms=" + autoEnterStableMilliseconds
                },
                new UTF8Encoding(false)
            );
        }

        public void Dispose()
        {
            foreach (PendingGestureRelease pending in pendingGestureReleases.ToArray())
            {
                CompleteGestureRelease(pending);
            }
            autoEnterController.Dispose();
            buttonSource.InputReceived -= OnInput;
            buttonSource.DevicesChanged -= OnInputDevicesChanged;
            buttonSource.Dispose();
            GC.SuppressFinalize(this);
        }

        private sealed class PendingGestureRelease
        {
            public System.Windows.Forms.Timer Timer;
            public KeyboardInput[] Inputs;
            public Action<bool> ReleaseCompleted;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInput
        {
            public uint Type;
            public KeyboardInputUnion Union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct KeyboardInputUnion
        {
            [FieldOffset(0)]
            public KeyboardInputData Keyboard;

            [FieldOffset(0)]
            public MouseInputData Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KeyboardInputData
        {
            public ushort VirtualKey;
            public ushort ScanCode;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MouseInputData
        {
            public int X;
            public int Y;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public UIntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint inputCount, KeyboardInput[] inputs, int inputSize);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);
    }

    internal sealed class KeyGestureCaptureForm : Form
    {
        private const int WmKeyDown = 0x0100;
        private const int WmKeyUp = 0x0101;
        private const int WmSysKeyDown = 0x0104;
        private const int WmSysKeyUp = 0x0105;
        private readonly Label valueLabel;
        private readonly Button recordButton;
        private readonly Button okButton;
        private readonly HashSet<int> captureDown;
        private readonly List<int> captureKeys;
        private bool recording;
        private KeyGesture gesture;

        public KeyGesture Gesture
        {
            get { return gesture; }
        }

        public KeyGestureCaptureForm()
        {
            captureDown = new HashSet<int>();
            captureKeys = new List<int>();
            Text = "自定义连接键映射";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 190);

            Label instruction = new Label {
                AutoSize = false,
                Location = new Point(22, 18),
                Size = new Size(396, 42),
                Text = "按住目标按键或组合键，全部松开后完成录制。\r\n最多可组合 6 个按键。"
            };
            valueLabel = new Label {
                AutoSize = false,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 12, FontStyle.Bold),
                Location = new Point(22, 66),
                Size = new Size(396, 42),
                TextAlign = ContentAlignment.MiddleCenter,
                Text = "等待按键…"
            };
            recordButton = new Button {
                Location = new Point(115, 132),
                Size = new Size(90, 32),
                Text = "重新录制"
            };
            okButton = new Button {
                Location = new Point(213, 132),
                Size = new Size(90, 32),
                Text = "确定",
                Enabled = false,
                DialogResult = DialogResult.OK
            };
            Button cancelButton = new Button {
                Location = new Point(311, 132),
                Size = new Size(90, 32),
                Text = "取消",
                DialogResult = DialogResult.Cancel
            };

            recordButton.Click += delegate { StartCapture(); };
            Controls.Add(instruction);
            Controls.Add(valueLabel);
            Controls.Add(recordButton);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
            Shown += delegate { StartCapture(); };
        }

        protected override void WndProc(ref Message message)
        {
            bool keyDown = message.Msg == WmKeyDown || message.Msg == WmSysKeyDown;
            bool keyUp = message.Msg == WmKeyUp || message.Msg == WmSysKeyUp;
            if (recording && (keyDown || keyUp))
            {
                CaptureKey(NormalizeModifierKey(message.WParam.ToInt32(), message.LParam), keyDown);
                return;
            }
            base.WndProc(ref message);
        }

        internal static int NormalizeModifierKey(int virtualKey, IntPtr messageData)
        {
            long value = messageData.ToInt64();
            int scanCode = (int)((value >> 16) & 0xFF);
            bool extended = (value & 0x01000000L) != 0;
            if (virtualKey == 0x10)
            {
                uint mapped = MapVirtualKey((uint)scanCode, 3);
                if (mapped == 0xA0 || mapped == 0xA1)
                {
                    return (int)mapped;
                }
            }
            if (virtualKey == 0x11)
            {
                return extended ? 0xA3 : 0xA2;
            }
            if (virtualKey == 0x12)
            {
                return extended ? 0xA5 : 0xA4;
            }
            return virtualKey;
        }

        private void StartCapture()
        {
            gesture = null;
            captureDown.Clear();
            captureKeys.Clear();
            recording = true;
            okButton.Enabled = false;
            recordButton.Enabled = false;
            valueLabel.Text = "等待按键…";
            Focus();
        }

        private void CaptureKey(int virtualKey, bool keyDown)
        {
            if (keyDown)
            {
                if (captureDown.Add(virtualKey) && !captureKeys.Contains(virtualKey) && captureKeys.Count < 6)
                {
                    captureKeys.Add(virtualKey);
                    valueLabel.Text = new KeyGesture(captureKeys).DisplayName;
                }
                return;
            }

            captureDown.Remove(virtualKey);
            if (captureDown.Count == 0 && captureKeys.Count > 0)
            {
                gesture = new KeyGesture(captureKeys);
                recording = false;
                valueLabel.Text = gesture.DisplayName;
                okButton.Enabled = true;
                recordButton.Enabled = true;
            }
        }

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);
    }
}
