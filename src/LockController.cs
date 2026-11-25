using System;
using System.Text;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 锁屏核心控制器。
    ///
    /// 锁定状态不做 DPMS 关屏，而是用一个全屏纯黑遮罩代替（视觉上同样是黑屏）。
    /// 原因：真关屏后唤醒必须点亮显示器，显示器状态切换会让 DWM 重建合成表面，
    /// 遮罩会丢失一瞬（人眼看到桌面闪一下、截屏也会抓到遮罩）。
    /// 纯黑遮罩常亮没有这个问题：磨砂提示界面能瞬间出现，截图始终干净。
    ///
    /// 两种形态：
    ///   Idle   —— 全屏纯黑、物理键鼠全部吞掉、鼠标穿透（自动化坐标点击照旧）
    ///   Prompt —— 半透明磨砂卡片 + 密码圆点 + 解锁/重新黑屏按钮
    ///
    /// 不调用 LockWorkStation、不切会话、不抢焦点，因此 UIA 元素树/截图/注入输入全程可用；
    /// 遮罩本身挂了 WDA_EXCLUDEFROMCAPTURE，任何截屏里都看不到它。
    /// </summary>
    internal sealed class LockController : IDisposable
    {
        private const int PromptTimeoutMs = 15000;

        private readonly InputHook _hook = new InputHook();
        private readonly Config _config;
        private readonly IntPtr _messageWindow;
        private readonly LockOverlayForm _overlay;
        private readonly StringBuilder _buffer = new StringBuilder();
        private bool _locked;
        private bool _prompting;
        private int _lastActivityTick;
        private Timer _restoreTimer;
        private Timer _idleTimer;

        public event Action Unlocked;
        public event Action<string> UnlockFailed;
        public event Action Locked;

        public bool IsLocked { get { return _locked; } }
        public bool HookOk { get { return _hook.IsInstalled; } }
        public bool IsPromptVisible { get { return _prompting; } }

        public LockController(Config config, IntPtr messageWindow, LockOverlayForm overlay)
        {
            _config = config;
            _messageWindow = messageWindow;
            _overlay = overlay;
            _hook.PhysicalKeyDown += OnPhysicalKeyDown;
            _hook.PhysicalMouseActivity += OnPhysicalMouseActivity;
            _overlay.SubmitRequested += OnSubmitRequested;
            _overlay.DismissRequested += OnDismissRequested;
        }

        public void Lock(int darkDelayMs)
        {
            if (_locked) return;
            _locked = true;
            _buffer.Length = 0;
            _prompting = false;

            if (!_hook.Install())
            {
                Log.Write("锁屏：键鼠钩子安装失败，物理输入不会被拦截");
                MessageBox.Show("键盘钩子安装失败，本次锁定无法拦截物理输入，也无法录入密码。\n请检查安全软件是否拦截了全局钩子。",
                    "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            _hook.SuppressMouse = true;
            _overlay.ShowOverlay();
            _overlay.EnterIdle();
            _lastActivityTick = Environment.TickCount;

            // 只阻止系统睡眠，不动显示器：遮罩本身负责“黑屏”的观感
            DisplayControl.KeepSystemAwake();
            StartIdleTimer();

            Log.Write("进入锁定状态，钩子=" + (_hook.IsInstalled ? "已安装" : "未安装"));
            if (Locked != null) Locked();
        }

        public void Unlock()
        {
            if (!_locked) return;
            _locked = false;
            _prompting = false;
            _buffer.Length = 0;

            StopTimer(ref _idleTimer);
            _hook.Uninstall();
            _overlay.HideOverlay();
            // 摘钩子、藏遮罩之后再补“弹起”：解锁这一下按键/点击的 up 事件已被遮罩吃掉，
            // 不补会让 Ctrl / Alt / 鼠标左键卡在按下状态，表现为解锁后键盘错乱、点不开东西。
            _hook.ReleaseStuckInput();

            // 防御：用户可能自己按过显示器电源，解锁时确保一定点亮
            DisplayControl.MonitorOn();
            DisplayControl.RequestDisplayOn();
            StartRestoreTimer();

            Log.Write("已解锁");
            if (Unlocked != null) Unlocked();
        }

        /// <summary>由隐藏消息窗口在 UI 线程上调用，处理锁屏状态下键入的字符。</summary>
        public void ProcessKey(Keys key, bool shift)
        {
            if (!_locked) return;

            WakeToPrompt();

            if (HandleKeyCore(key, shift, true))
            {
                Unlock();
                return;
            }
            _overlay.SetDotCount(_buffer.Length);
        }

        /// <summary>由隐藏消息窗口在 UI 线程上调用：物理鼠标有动静。</summary>
        public void ProcessMouseActivity()
        {
            if (!_locked) return;
            WakeToPrompt();
        }

        internal bool SelfTestKey(Keys key, bool shift)
        {
            if (HandleKeyCore(key, shift, false)) return true;
            return false;
        }

        internal void SelfTestReset()
        {
            _buffer.Length = 0;
        }

        private void WakeToPrompt()
        {
            _lastActivityTick = Environment.TickCount;
            if (_prompting) return;

            _prompting = true;
            _overlay.EnterPrompt();
            _overlay.SetDotCount(_buffer.Length);
            // 提示界面里要能点“解锁/重新黑屏”，这一阶段放行物理鼠标
            _hook.SuppressMouse = false;
            Log.Write("显示密码输入界面");
        }

        private void BackToIdle()
        {
            if (!_locked || !_prompting) return;
            _prompting = false;
            _overlay.EnterIdle();
            _hook.SuppressMouse = true;
            Log.Write("回到纯黑状态");
        }

        private bool HandleKeyCore(Keys key, bool shift, bool allowSound)
        {
            if (key == Keys.Enter)
                return Commit(allowSound);

            if (key == Keys.Back)
            {
                if (_buffer.Length > 0) _buffer.Length -= 1;
                return false;
            }
            if (key == Keys.Escape)
            {
                _buffer.Length = 0;
                return false;
            }

            char c = KeyMapper.ToChar(key, shift);
            if (c != '\0' && _buffer.Length < 64)
                _buffer.Append(c);
            return false;
        }

        private bool Commit(bool allowSound)
        {
            string password = _buffer.ToString();
            _buffer.Length = 0;

            if (_config.VerifyPassword(password))
                return true;

            if (allowSound && _config.SoundOnError)
                NativeMethods.MessageBeep(NativeMethods.MB_ICONHAND);

            string message = password.Length == 0 ? "未输入密码" : "密码错误";
            if (UnlockFailed != null)
                UnlockFailed(message);

            if (_locked && _prompting)
                _overlay.ShowError(message + "，请重新输入", 2600);

            return false;
        }

        private void OnSubmitRequested()
        {
            if (!_locked) return;
            if (Commit(true)) Unlock();
        }

        private void OnDismissRequested()
        {
            BackToIdle();
        }

        private void OnPhysicalKeyDown(Keys key, bool shift)
        {
            // 注意：此处运行在低级钩子回调栈内，只能做最轻量的转发。
            if (_messageWindow == IntPtr.Zero) return;
            NativeMethods.PostMessage(
                _messageWindow,
                MessageWindow.WM_APP_KEY,
                (IntPtr)(int)key,
                (IntPtr)(shift ? 1 : 0));
        }

        private void OnPhysicalMouseActivity()
        {
            if (_messageWindow == IntPtr.Zero) return;
            NativeMethods.PostMessage(
                _messageWindow,
                MessageWindow.WM_APP_MOUSE,
                IntPtr.Zero,
                IntPtr.Zero);
        }

        private void StartIdleTimer()
        {
            StopTimer(ref _idleTimer);
            _idleTimer = new Timer();
            _idleTimer.Interval = 1000;
            _idleTimer.Tick += OnIdleTick;
            _idleTimer.Start();
        }

        private void OnIdleTick(object sender, EventArgs e)
        {
            if (!_locked) { StopTimer(ref _idleTimer); return; }
            if (!_prompting) return;

            int idle = Environment.TickCount - _lastActivityTick;
            if (idle >= PromptTimeoutMs)
                BackToIdle();
        }

        private void StartRestoreTimer()
        {
            StopTimer(ref _restoreTimer);
            _restoreTimer = new Timer();
            _restoreTimer.Interval = 800;
            _restoreTimer.Tick += OnRestoreTick;
            _restoreTimer.Start();
        }

        private void OnRestoreTick(object sender, EventArgs e)
        {
            StopTimer(ref _restoreTimer);
            if (!_locked) DisplayControl.ReleaseExecutionState();
        }

        private static void StopTimer(ref Timer timer)
        {
            if (timer == null) return;
            try
            {
                timer.Stop();
                timer.Dispose();
            }
            catch { }
            timer = null;
        }

        public void Dispose()
        {
            if (_locked)
            {
                _locked = false;
                _prompting = false;
                _hook.Uninstall();
                _overlay.HideOverlay();
                _hook.ReleaseStuckInput();
                DisplayControl.MonitorOn();
                DisplayControl.ReleaseExecutionState();
            }
            StopTimer(ref _restoreTimer);
            StopTimer(ref _idleTimer);
            _hook.PhysicalKeyDown -= OnPhysicalKeyDown;
            _hook.PhysicalMouseActivity -= OnPhysicalMouseActivity;
            _hook.Dispose();
            _overlay.SubmitRequested -= OnSubmitRequested;
            _overlay.DismissRequested -= OnDismissRequested;
        }
    }
}
