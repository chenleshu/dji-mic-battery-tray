using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Forms;

namespace DjiMicBattery
{
    internal static class AutoEnterTiming
    {
        public const int DefaultTimeoutMilliseconds = 20000;
        public const int DefaultStableMilliseconds = 800;
        public const int MinimumTimeoutMilliseconds = 2000;
        public const int MaximumTimeoutMilliseconds = 120000;
        public const int MinimumStableMilliseconds = 200;
        public const int MaximumStableMilliseconds = 5000;

        public static int NormalizeTimeout(int value)
        {
            return Math.Max(MinimumTimeoutMilliseconds, Math.Min(MaximumTimeoutMilliseconds, value));
        }

        public static int NormalizeStable(int value)
        {
            return Math.Max(MinimumStableMilliseconds, Math.Min(MaximumStableMilliseconds, value));
        }

        public static string FormatSeconds(int milliseconds)
        {
            return (milliseconds / 1000.0).ToString("0.0") + " 秒";
        }
    }

    internal struct TextFingerprint : IEquatable<TextFingerprint>
    {
        private readonly int length;
        private readonly ulong hash;

        public int Length { get { return length; } }
        public ulong Hash { get { return hash; } }

        public TextFingerprint(int length, ulong hash)
        {
            this.length = length;
            this.hash = hash;
        }

        public static TextFingerprint FromText(string text)
        {
            string value = text ?? "";
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong valueHash = offset;
            for (int i = 0; i < value.Length; i++)
            {
                valueHash ^= value[i];
                valueHash *= prime;
            }
            return new TextFingerprint(value.Length, valueHash);
        }

        public bool Equals(TextFingerprint other)
        {
            return length == other.length && hash == other.hash;
        }

        public override bool Equals(object obj)
        {
            return obj is TextFingerprint && Equals((TextFingerprint)obj);
        }

        public override int GetHashCode()
        {
            return length ^ hash.GetHashCode();
        }
    }

    internal sealed class TextStabilityTracker
    {
        private readonly TextFingerprint baseline;
        private readonly int stableMilliseconds;
        private bool changed;
        private TextFingerprint last;
        private int lastChangedAt;

        public TextStabilityTracker(TextFingerprint baseline, int stableMilliseconds)
        {
            this.baseline = baseline;
            this.stableMilliseconds = AutoEnterTiming.NormalizeStable(stableMilliseconds);
        }

        public bool Observe(TextFingerprint value, int elapsedMilliseconds)
        {
            if (!changed)
            {
                if (value.Equals(baseline))
                {
                    return false;
                }
                changed = true;
                last = value;
                lastChangedAt = elapsedMilliseconds;
                return false;
            }

            if (!value.Equals(last))
            {
                last = value;
                lastChangedAt = elapsedMilliseconds;
                return false;
            }

            return elapsedMilliseconds - lastChangedAt >= stableMilliseconds;
        }
    }

    internal enum TypelessAutoEnterPhase
    {
        Idle,
        Recording,
        AwaitingCompletion
    }

    internal sealed class TypelessAutoEnterController : IDisposable
    {
        private const int PollMilliseconds = 100;
        private const int MaximumObservedTextLength = 65536;

        private readonly object gate;
        private readonly Control dispatcher;
        private readonly Func<bool> submitEnter;
        private RecognitionSession activeSession;
        private bool disposed;
        private TypelessAutoEnterPhase phase;
        private string state;
        private string lastSubmittedAt;
        private int submitCount;

        public string State
        {
            get { lock (gate) { return state; } }
        }

        public string LastSubmittedAt
        {
            get { lock (gate) { return lastSubmittedAt; } }
        }

        public int SubmitCount
        {
            get { lock (gate) { return submitCount; } }
        }

        public TypelessAutoEnterPhase Phase
        {
            get { lock (gate) { return phase; } }
        }

        public bool IsRecording
        {
            get { lock (gate) { return phase == TypelessAutoEnterPhase.Recording; } }
        }

        public TypelessAutoEnterController(Control dispatcher, Func<bool> submitEnter)
        {
            if (dispatcher == null) throw new ArgumentNullException("dispatcher");
            if (submitEnter == null) throw new ArgumentNullException("submitEnter");
            gate = new object();
            this.dispatcher = dispatcher;
            this.submitEnter = submitEnter;
            phase = TypelessAutoEnterPhase.Idle;
            state = "待命";
            lastSubmittedAt = "";
        }

        public void BeginRecording(int timeoutMilliseconds, int stableMilliseconds)
        {
            // This method is deliberately synchronous: it is called immediately before
            // Right Alt is injected, so the target window, focused editor and baseline
            // cannot be captured later from Typeless or from another focused control.
            RecognitionSession previous;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                previous = activeSession;
                activeSession = null;
                phase = TypelessAutoEnterPhase.Idle;
            }
            ReleaseSession(previous);

            RecognitionSession session = CaptureSession(timeoutMilliseconds, stableMilliseconds);
            lock (gate)
            {
                if (disposed)
                {
                    session.DisposeCancellationSignal();
                    return;
                }
                activeSession = session;
                phase = TypelessAutoEnterPhase.Recording;
                state = session.CanMonitor ? "正在识别" : session.CaptureFailure;
            }
        }

        public bool PrepareFinishRecording()
        {
            RecognitionSession session;
            lock (gate)
            {
                session = activeSession;
                if (session == null || disposed || phase != TypelessAutoEnterPhase.Recording)
                {
                    return false;
                }
            }

            if (!session.CanMonitor)
            {
                return FailFinishPreparation(session, session.CaptureFailure);
            }
            if (GetForegroundWindow() != session.TargetWindow)
            {
                return FailFinishPreparation(session, "前台窗口已切换，未启用自动回车");
            }

            try
            {
                AutomationElement target;
                TextFingerprint stopBaseline;
                if (!TryResolveFocusedTarget(session, out target, out stopBaseline) ||
                    GetForegroundWindow() != session.TargetWindow)
                {
                    return FailFinishPreparation(session, "输入焦点或输入框已切换，未启用自动回车");
                }
                lock (gate)
                {
                    if (disposed || activeSession != session ||
                        phase != TypelessAutoEnterPhase.Recording)
                    {
                        return false;
                    }
                    session.SetStopBaseline(stopBaseline);
                    state = "正在结束识别";
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                return FailFinishPreparation(session, "输入框已失效，未启用自动回车");
            }
            catch (Exception)
            {
                return FailFinishPreparation(session, "识别状态读取失败");
            }
        }

        public void FinishRecording()
        {
            RecognitionSession session;
            Thread worker = null;
            lock (gate)
            {
                session = activeSession;
                if (session == null || disposed || phase != TypelessAutoEnterPhase.Recording)
                {
                    return;
                }

                if (!session.CanMonitor || !session.StopPrepared)
                {
                    activeSession = null;
                    phase = TypelessAutoEnterPhase.Idle;
                    state = !session.CanMonitor
                        ? session.CaptureFailure
                        : (string.IsNullOrEmpty(session.StopFailure)
                            ? "停止前未确认输入框，未按回车"
                            : session.StopFailure);
                }
                else
                {
                    phase = TypelessAutoEnterPhase.AwaitingCompletion;
                    state = "等待文字补全";
                    worker = new Thread(new ThreadStart(delegate { RunSession(session); }));
                    worker.IsBackground = true;
                    worker.Name = "Typeless completion monitor";
                    worker.SetApartmentState(ApartmentState.MTA);
                    session.Worker = worker;
                }
            }

            if (worker == null)
            {
                session.DisposeCancellationSignal();
                return;
            }

            try
            {
                // The worker and its stability clock start only after the mapped
                // Right Alt has been fully released by DjiButtonRemapper.
                worker.Start();
            }
            catch (Exception)
            {
                CompleteSession(session, "识别监控启动失败");
                session.DisposeCancellationSignal();
            }
        }

        public void Cancel(string nextState)
        {
            RecognitionSession session;
            lock (gate)
            {
                session = activeSession;
                activeSession = null;
                phase = TypelessAutoEnterPhase.Idle;
                state = string.IsNullOrWhiteSpace(nextState) ? "已取消" : nextState;
            }
            ReleaseSession(session);
        }

        private void RunSession(RecognitionSession session)
        {
            try
            {
                // No observations made while recording count toward the stable period.
                // Both the timeout and the stability timeline begin after stop/release.
                Stopwatch watch = Stopwatch.StartNew();
                TextStabilityTracker tracker = new TextStabilityTracker(session.Baseline, session.StableMilliseconds);
                while (watch.ElapsedMilliseconds < session.TimeoutMilliseconds)
                {
                    if (session.WaitForCancellation(PollMilliseconds) || !IsAwaitingCompletion(session))
                    {
                        return;
                    }
                    if (GetForegroundWindow() != session.TargetWindow)
                    {
                        CompleteSession(session, "前台窗口已切换，未按回车");
                        return;
                    }

                    TextFingerprint current;
                    AutomationElement target;
                    if (!TryResolveFocusedTarget(session, out target, out current))
                    {
                        CompleteSession(session, "输入焦点或输入框已切换，未按回车");
                        return;
                    }
                    if (tracker.Observe(current, (int)watch.ElapsedMilliseconds))
                    {
                        RequestSubmit(session, current);
                        return;
                    }
                }
                CompleteSession(session, "等待超时，未按回车");
            }
            catch (ElementNotAvailableException)
            {
                CompleteSession(session, "输入框已失效，未按回车");
            }
            catch (Exception)
            {
                CompleteSession(session, "识别状态读取失败");
            }
            finally
            {
                session.DisposeCancellationSignal();
            }
        }

        private void RequestSubmit(RecognitionSession session, TextFingerprint stableFingerprint)
        {
            try
            {
                dispatcher.BeginInvoke(new Action(delegate { SubmitIfStillSafe(session, stableFingerprint); }));
            }
            catch (InvalidOperationException)
            {
                CompleteSession(session, "应用正在退出");
            }
        }

        private void SubmitIfStillSafe(RecognitionSession session, TextFingerprint stableFingerprint)
        {
            try
            {
                lock (gate)
                {
                    if (disposed || activeSession != session ||
                        phase != TypelessAutoEnterPhase.AwaitingCompletion)
                    {
                        return;
                    }
                }
                if (GetForegroundWindow() != session.TargetWindow)
                {
                    CompleteSession(session, "前台窗口已切换，未按回车");
                    return;
                }

                // UI-thread validation is intentionally repeated immediately before
                // SendInput. A queued callback must never submit into a newly focused
                // editor or after Chromium has changed the text again.
                AutomationElement target;
                TextFingerprint current;
                if (!TryResolveFocusedTarget(session, out target, out current))
                {
                    CompleteSession(session, "输入焦点或输入框已切换，未按回车");
                    return;
                }
                if (!current.Equals(stableFingerprint) || GetForegroundWindow() != session.TargetWindow)
                {
                    CompleteSession(session, "文字仍在变化或窗口已切换，未按回车");
                    return;
                }
                lock (gate)
                {
                    if (disposed || activeSession != session ||
                        phase != TypelessAutoEnterPhase.AwaitingCompletion)
                    {
                        return;
                    }
                }

                bool accepted = submitEnter();
                lock (gate)
                {
                    if (activeSession == session)
                    {
                        activeSession = null;
                        phase = TypelessAutoEnterPhase.Idle;
                        state = accepted ? "文字已补全，已按回车" : "回车注入失败";
                        if (accepted)
                        {
                            submitCount++;
                            lastSubmittedAt = DateTime.Now.ToString("o");
                        }
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                CompleteSession(session, "输入框已失效，未按回车");
            }
            catch (Exception)
            {
                CompleteSession(session, "回车前安全检查失败");
            }
        }

        private void CompleteSession(RecognitionSession session, string finalState)
        {
            lock (gate)
            {
                if (activeSession == session)
                {
                    activeSession = null;
                    phase = TypelessAutoEnterPhase.Idle;
                    state = finalState;
                }
            }
        }

        private RecognitionSession CaptureSession(int timeoutMilliseconds, int stableMilliseconds)
        {
            IntPtr targetWindow = GetForegroundWindow();
            int timeout = AutoEnterTiming.NormalizeTimeout(timeoutMilliseconds);
            int stable = AutoEnterTiming.NormalizeStable(stableMilliseconds);
            if (!IsTypelessRunning())
            {
                return RecognitionSession.Invalid(targetWindow, timeout, stable, "未检测到 Typeless");
            }
            if (targetWindow == IntPtr.Zero)
            {
                return RecognitionSession.Invalid(targetWindow, timeout, stable, "未找到目标窗口");
            }

            try
            {
                AutomationElement target = FindReadableTextTarget(AutomationElement.FocusedElement);
                TextFingerprint baseline;
                if (target == null || !TryReadFingerprint(target, out baseline))
                {
                    return RecognitionSession.Invalid(targetWindow, timeout, stable, "当前输入框不可识别");
                }
                TargetIdentity identity = TargetIdentity.Capture(target);
                if (identity == null || GetForegroundWindow() != targetWindow)
                {
                    return RecognitionSession.Invalid(targetWindow, timeout, stable, "目标窗口已切换");
                }
                return RecognitionSession.Valid(targetWindow, timeout, stable, target, identity, baseline);
            }
            catch (ElementNotAvailableException)
            {
                return RecognitionSession.Invalid(targetWindow, timeout, stable, "当前输入框不可识别");
            }
            catch (Exception)
            {
                return RecognitionSession.Invalid(targetWindow, timeout, stable, "识别状态读取失败");
            }
        }

        private bool IsAwaitingCompletion(RecognitionSession session)
        {
            lock (gate)
            {
                return !disposed && activeSession == session &&
                    phase == TypelessAutoEnterPhase.AwaitingCompletion;
            }
        }

        private bool FailFinishPreparation(RecognitionSession session, string failure)
        {
            lock (gate)
            {
                if (activeSession == session && phase == TypelessAutoEnterPhase.Recording)
                {
                    session.SetStopFailure(failure);
                    state = failure;
                }
            }
            return false;
        }

        private static void ReleaseSession(RecognitionSession session)
        {
            if (session == null)
            {
                return;
            }
            session.RequestCancellation();
            if (session.Worker == null)
            {
                session.DisposeCancellationSignal();
            }
        }

        private static bool TryReadFingerprint(AutomationElement element, out TextFingerprint fingerprint)
        {
            string text;
            object pattern;
            ControlType controlType = element.Current.ControlType;
            if ((controlType == ControlType.Edit || controlType == ControlType.Document) &&
                element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
            {
                ValuePattern valuePattern = (ValuePattern)pattern;
                if (!valuePattern.Current.IsReadOnly)
                {
                    text = valuePattern.Current.Value;
                    fingerprint = TextFingerprint.FromText(text);
                    return true;
                }
            }
            if (controlType == ControlType.Edit &&
                element.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
            {
                text = ((TextPattern)pattern).DocumentRange.GetText(MaximumObservedTextLength);
                fingerprint = TextFingerprint.FromText(text);
                return true;
            }
            fingerprint = new TextFingerprint();
            return false;
        }

        private static AutomationElement FindReadableTextTarget(AutomationElement focused)
        {
            AutomationElement current = focused;
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; current != null && depth < 8; depth++)
            {
                TextFingerprint ignored;
                if (TryReadFingerprint(current, out ignored))
                {
                    return current;
                }
                current = walker.GetParent(current);
            }
            return null;
        }

        private static bool TryResolveFocusedTarget(
            RecognitionSession session,
            out AutomationElement target,
            out TextFingerprint fingerprint)
        {
            target = null;
            fingerprint = new TextFingerprint();
            AutomationElement focused = AutomationElement.FocusedElement;
            if (focused == null)
            {
                return false;
            }

            AutomationElement currentTarget = session.Target;
            try
            {
                if (currentTarget != null && IsSameOrDescendant(currentTarget, focused) &&
                    TryReadFingerprint(currentTarget, out fingerprint))
                {
                    target = currentTarget;
                    return true;
                }
            }
            catch (ElementNotAvailableException)
            {
                // Chromium can replace the editable accessibility node while keeping
                // focus. Relocation below is accepted only when its stable identity
                // or screen geometry still matches the original captured editor.
            }

            AutomationElement relocated = FindReadableTextTarget(focused);
            if (relocated == null || !session.Identity.Matches(relocated) ||
                !TryReadFingerprint(relocated, out fingerprint))
            {
                return false;
            }
            session.Target = relocated;
            target = relocated;
            return true;
        }

        private static bool IsSameOrDescendant(AutomationElement target, AutomationElement focused)
        {
            AutomationElement current = focused;
            TreeWalker walker = TreeWalker.ControlViewWalker;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                if (Automation.Compare(target, current))
                {
                    return true;
                }
                current = walker.GetParent(current);
            }
            return false;
        }

        private sealed class TargetIdentity
        {
            private readonly string automationId;
            private readonly string className;
            private readonly string frameworkId;
            private readonly int controlTypeId;
            private readonly int processId;
            private readonly int nativeWindowHandle;
            private readonly System.Windows.Rect bounds;

            private TargetIdentity(AutomationElement.AutomationElementInformation information)
            {
                automationId = information.AutomationId ?? "";
                className = information.ClassName ?? "";
                frameworkId = information.FrameworkId ?? "";
                controlTypeId = information.ControlType.Id;
                processId = information.ProcessId;
                nativeWindowHandle = information.NativeWindowHandle;
                bounds = information.BoundingRectangle;
            }

            public static TargetIdentity Capture(AutomationElement element)
            {
                return element == null ? null : new TargetIdentity(element.Current);
            }

            public bool Matches(AutomationElement candidate)
            {
                AutomationElement.AutomationElementInformation information = candidate.Current;
                if (information.ControlType.Id != controlTypeId)
                {
                    return false;
                }
                if (processId <= 0 || information.ProcessId != processId)
                {
                    return false;
                }
                if (!string.IsNullOrEmpty(frameworkId) &&
                    !string.Equals(frameworkId, information.FrameworkId ?? "", StringComparison.Ordinal))
                {
                    return false;
                }
                if (!string.IsNullOrEmpty(automationId))
                {
                    return string.Equals(automationId, information.AutomationId ?? "", StringComparison.Ordinal);
                }
                if (nativeWindowHandle != 0 && information.NativeWindowHandle != 0)
                {
                    if (nativeWindowHandle != information.NativeWindowHandle)
                    {
                        return false;
                    }
                    return BoundsLikelyIdentifySameEditor(bounds, information.BoundingRectangle);
                }
                if (!string.IsNullOrEmpty(className) &&
                    !string.Equals(className, information.ClassName ?? "", StringComparison.Ordinal))
                {
                    return false;
                }
                return BoundsLikelyIdentifySameEditor(bounds, information.BoundingRectangle);
            }

            private static bool BoundsLikelyIdentifySameEditor(System.Windows.Rect original, System.Windows.Rect candidate)
            {
                if (original.IsEmpty || candidate.IsEmpty || original.Width <= 0 || candidate.Width <= 0)
                {
                    return false;
                }
                double overlap = Math.Max(0, Math.Min(original.Right, candidate.Right) -
                    Math.Max(original.Left, candidate.Left));
                double minimumWidth = Math.Min(original.Width, candidate.Width);
                if (overlap / minimumWidth < 0.8)
                {
                    return false;
                }
                double verticalOverlap = Math.Max(0, Math.Min(original.Bottom, candidate.Bottom) -
                    Math.Max(original.Top, candidate.Top));
                return verticalOverlap > 0 || Math.Abs(original.Bottom - candidate.Bottom) <= 32 ||
                    Math.Abs(original.Top - candidate.Top) <= 32;
            }
        }

        private static bool IsTypelessRunning()
        {
            Process[] processes = Process.GetProcessesByName("Typeless");
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }

        public void Dispose()
        {
            RecognitionSession session;
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
                session = activeSession;
                activeSession = null;
                phase = TypelessAutoEnterPhase.Idle;
                state = "已停止";
            }
            ReleaseSession(session);
            GC.SuppressFinalize(this);
        }

        private sealed class RecognitionSession
        {
            public readonly IntPtr TargetWindow;
            public readonly int TimeoutMilliseconds;
            public readonly int StableMilliseconds;
            public TextFingerprint Baseline;
            public readonly TargetIdentity Identity;
            public readonly string CaptureFailure;
            public AutomationElement Target;
            public Thread Worker;
            public bool StopPrepared { get; private set; }
            public string StopFailure { get; private set; }
            private readonly object cancellationGate;
            private EventWaitHandle cancellationEvent;

            public bool CanMonitor
            {
                get
                {
                    return TargetWindow != IntPtr.Zero && Target != null && Identity != null &&
                        string.IsNullOrEmpty(CaptureFailure);
                }
            }

            private RecognitionSession(
                IntPtr targetWindow,
                int timeoutMilliseconds,
                int stableMilliseconds,
                AutomationElement target,
                TargetIdentity identity,
                TextFingerprint baseline,
                string captureFailure)
            {
                TargetWindow = targetWindow;
                TimeoutMilliseconds = timeoutMilliseconds;
                StableMilliseconds = stableMilliseconds;
                Target = target;
                Identity = identity;
                Baseline = baseline;
                CaptureFailure = captureFailure ?? "";
                StopFailure = "";
                cancellationGate = new object();
                cancellationEvent = new EventWaitHandle(false, EventResetMode.ManualReset);
            }

            public static RecognitionSession Valid(
                IntPtr targetWindow,
                int timeoutMilliseconds,
                int stableMilliseconds,
                AutomationElement target,
                TargetIdentity identity,
                TextFingerprint baseline)
            {
                return new RecognitionSession(
                    targetWindow,
                    timeoutMilliseconds,
                    stableMilliseconds,
                    target,
                    identity,
                    baseline,
                    ""
                );
            }

            public static RecognitionSession Invalid(
                IntPtr targetWindow,
                int timeoutMilliseconds,
                int stableMilliseconds,
                string captureFailure)
            {
                return new RecognitionSession(
                    targetWindow,
                    timeoutMilliseconds,
                    stableMilliseconds,
                    null,
                    null,
                    new TextFingerprint(),
                    captureFailure
                );
            }

            public void RequestCancellation()
            {
                lock (cancellationGate)
                {
                    if (cancellationEvent != null)
                    {
                        cancellationEvent.Set();
                    }
                }
            }

            public void SetStopBaseline(TextFingerprint baseline)
            {
                Baseline = baseline;
                StopPrepared = true;
                StopFailure = "";
            }

            public void SetStopFailure(string failure)
            {
                StopPrepared = false;
                StopFailure = string.IsNullOrWhiteSpace(failure)
                    ? "停止前未确认输入框，未按回车"
                    : failure;
            }

            public bool WaitForCancellation(int timeoutMilliseconds)
            {
                EventWaitHandle waitHandle;
                lock (cancellationGate)
                {
                    waitHandle = cancellationEvent;
                }
                if (waitHandle == null)
                {
                    return true;
                }
                try
                {
                    return waitHandle.WaitOne(timeoutMilliseconds);
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            }

            public void DisposeCancellationSignal()
            {
                lock (cancellationGate)
                {
                    if (cancellationEvent != null)
                    {
                        cancellationEvent.Dispose();
                        cancellationEvent = null;
                    }
                }
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();
    }

    internal sealed class AutoEnterSettingsForm : Form
    {
        private readonly CheckBox enabledCheckBox;
        private readonly NumericUpDown timeoutSeconds;
        private readonly NumericUpDown stableSeconds;

        public bool AutoEnterEnabled { get { return enabledCheckBox.Checked; } }
        public int TimeoutMilliseconds { get { return DecimalSecondsToMilliseconds(timeoutSeconds.Value); } }
        public int StableMilliseconds { get { return DecimalSecondsToMilliseconds(stableSeconds.Value); } }

        public AutoEnterSettingsForm(bool enabled, int timeoutMilliseconds, int stableMilliseconds)
        {
            Text = "Typeless 自动回车设置";
            Font = SystemFonts.MessageBoxFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(460, 250);

            enabledCheckBox = new CheckBox {
                AutoSize = true,
                Location = new Point(24, 20),
                Text = "识别文字补全后自动按一次回车",
                Checked = enabled
            };
            Label timeoutLabel = new Label {
                AutoSize = false,
                Location = new Point(24, 62),
                Size = new Size(210, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "最长等待时间（秒）"
            };
            timeoutSeconds = CreateSecondsInput(
                new Point(248, 62),
                AutoEnterTiming.MinimumTimeoutMilliseconds,
                AutoEnterTiming.MaximumTimeoutMilliseconds,
                AutoEnterTiming.NormalizeTimeout(timeoutMilliseconds)
            );
            Label stableLabel = new Label {
                AutoSize = false,
                Location = new Point(24, 101),
                Size = new Size(210, 28),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "文字停止变化后再等待（秒）"
            };
            stableSeconds = CreateSecondsInput(
                new Point(248, 101),
                AutoEnterTiming.MinimumStableMilliseconds,
                AutoEnterTiming.MaximumStableMilliseconds,
                AutoEnterTiming.NormalizeStable(stableMilliseconds)
            );
            Label explanation = new Label {
                AutoSize = false,
                Location = new Point(24, 142),
                Size = new Size(412, 45),
                ForeColor = SystemColors.GrayText,
                Text = "程序只比较原输入框的文字是否变化，不保存文字内容。\r\n焦点或前台窗口发生变化时不会自动按回车。"
            };
            Button okButton = new Button {
                Location = new Point(248, 204),
                Size = new Size(88, 30),
                Text = "确定",
                DialogResult = DialogResult.OK
            };
            Button cancelButton = new Button {
                Location = new Point(348, 204),
                Size = new Size(88, 30),
                Text = "取消",
                DialogResult = DialogResult.Cancel
            };

            Controls.Add(enabledCheckBox);
            Controls.Add(timeoutLabel);
            Controls.Add(timeoutSeconds);
            Controls.Add(stableLabel);
            Controls.Add(stableSeconds);
            Controls.Add(explanation);
            Controls.Add(okButton);
            Controls.Add(cancelButton);
            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static NumericUpDown CreateSecondsInput(Point location, int minimumMs, int maximumMs, int valueMs)
        {
            return new NumericUpDown {
                Location = location,
                Size = new Size(110, 28),
                DecimalPlaces = 1,
                Increment = 0.1M,
                Minimum = minimumMs / 1000.0M,
                Maximum = maximumMs / 1000.0M,
                Value = valueMs / 1000.0M
            };
        }

        private static int DecimalSecondsToMilliseconds(decimal seconds)
        {
            return Decimal.ToInt32(Decimal.Round(seconds * 1000M, 0));
        }
    }
}
