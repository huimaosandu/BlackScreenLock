using System;
using System.Text;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 锁屏核心控制器。
    /// 锁屏 = 安装低级键鼠钩子（吞掉物理输入，放行注入输入）+ 关闭显示器（DPMS）。
    /// 解锁 = 卸载钩子 + 点亮显示器。
    /// 整个过程中不锁屏（不调用 LockWorkStation）、不切换桌面、不显示全屏遮挡窗口，
    /// 所以 UIA 元素树、窗口消息、截图全部保持可用。
    /// </summary>
    internal sealed class LockController : IDisposable
    {
        private readonly InputHook _hook = new InputHook();
        private readonly Config _config;
        private readonly IntPtr _messageWindow;
        private readonly StringBuilder _buffer = new StringBuilder();
        private bool _locked;
        private Timer _guardTimer;
        private Timer _darkTimer;
        private Timer _restoreTimer;

        public event Action Unlocked;
        public event Action<string> UnlockFailed;
        public event Action Locked;

        public bool IsLocked { get { return _locked; } }
        public bool HookOk { get { return _hook.IsInstalled; } }

        public LockController(Config config, IntPtr messageWindow)
        {
            _config = config;
            _messageWindow = messageWindow;
            _hook.PhysicalKeyDown += OnPhysicalKeyDown;
        }

        /// <summary>
        /// 进入锁定。<paramref name="darkDelayMs"/> 用于让提示气泡先显示一小会儿再关屏。
        /// </summary>
        public void Lock(int darkDelayMs)
        {
            if (_locked) return;
            _locked = true;
            _buffer.Length = 0;

            if (!_hook.Install())
            {
                Log.Write("锁屏：键鼠钩子安装失败，物理输入不会被拦截（仅关显示器）");
                MessageBox.Show("键盘钩子安装失败，本次锁定无法拦截物理输入，屏幕仍会关闭。\n请检查安全软件是否拦截了全局钩子。",
                    "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            DisplayControl.KeepSystemAwake();
            if (_config.KeepDisplayOff) StartGuardTimer();

            if (darkDelayMs > 0) StartDarkTimer(darkDelayMs);
            else DisplayControl.MonitorOff();

            Log.Write("进入锁定状态，钩子=" + (_hook.IsInstalled ? "已安装" : "未安装"));
            if (Locked != null) Locked();
        }

        public void Unlock()
        {
            if (!_locked) return;
            _locked = false;
            _buffer.Length = 0;

            StopTimer(ref _darkTimer);
            StopTimer(ref _guardTimer);
            _hook.Uninstall();

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
            if (HandleKeyCore(key, shift, true)) Unlock();
        }

        /// <summary>仅供自检调用：复用真实按键处理逻辑，但要求已通过锁定标志，故不改变运行状态。</summary>
        internal bool SelfTestKey(Keys key, bool shift)
        {
            if (HandleKeyCore(key, shift, false)) return true;
            return false;
        }

        internal void SelfTestReset()
        {
            _buffer.Length = 0;
        }

        /// <summary>返回 true 表示本次按键提交了正确密码。</summary>
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

            if (UnlockFailed != null)
                UnlockFailed(password.Length == 0 ? "未输入密码" : "密码错误");
            return false;
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

        private void StartDarkTimer(int delayMs)
        {
            StopTimer(ref _darkTimer);
            _darkTimer = new Timer();
            _darkTimer.Interval = delayMs < 100 ? 100 : delayMs;
            _darkTimer.Tick += OnDarkTick;
            _darkTimer.Start();
        }

        private void OnDarkTick(object sender, EventArgs e)
        {
            StopTimer(ref _darkTimer);
            if (_locked) DisplayControl.MonitorOff();
        }

        private void StartGuardTimer()
        {
            StopTimer(ref _guardTimer);
            _guardTimer = new Timer();
            _guardTimer.Interval = 1000;
            _guardTimer.Tick += OnGuardTick;
            _guardTimer.Start();
        }

        private void OnGuardTick(object sender, EventArgs e)
        {
            if (!_locked) { StopTimer(ref _guardTimer); return; }
            // 兜底：若显示器被其他程序/系统策略点亮，重新熄灭
            DisplayControl.MonitorOff();
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
                _hook.Uninstall();
                DisplayControl.MonitorOn();
                DisplayControl.ReleaseExecutionState();
            }
            StopTimer(ref _darkTimer);
            StopTimer(ref _guardTimer);
            StopTimer(ref _restoreTimer);
            _hook.PhysicalKeyDown -= OnPhysicalKeyDown;
            _hook.Dispose();
        }
    }
}
