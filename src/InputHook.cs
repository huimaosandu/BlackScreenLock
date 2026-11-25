using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 低级键鼠钩子。两个关键设计：
    /// 1) 只拦截“物理”输入，带 LLKHF_INJECTED / LLMHF_INJECTED 标记的输入一律放行，
    ///    因此自动化工具（SendInput / UIA 模拟点击）在锁屏期间照常工作，不会被本工具打断。
    /// 2) 回调里只做最轻量的工作并向上层抛事件，上层必须通过 PostMessage 转发，
    ///    避免在钩子栈内卸载钩子造成死锁。
    /// </summary>
    internal sealed class InputHook : IDisposable
    {
        private readonly NativeMethods.LowLevelProc _keyboardProc;
        private readonly NativeMethods.LowLevelProc _mouseProc;
        private IntPtr _keyboardHook = IntPtr.Zero;
        private IntPtr _mouseHook = IntPtr.Zero;
        private bool _shiftDown;
        private int _lastMouseNotifyTick;

        /// <summary>
        /// 是否处于“拦截”状态。卸载时先置 false 再摘钩子：
        /// 即使 UnhookWindowsHookEx 失败（钩子仍挂在钩子链上），回调也会直接放行，
        /// 不会把系统键盘/鼠标持续吞掉——这是防止解锁后输入失灵的最后一道保险。
        /// </summary>
        private volatile bool _active;

        /// <summary>物理按键按下（虚拟键, Shift 是否按下）。</summary>
        public event Action<Keys, bool> PhysicalKeyDown;

        /// <summary>物理鼠标活动（移动/按下/滚轮），已节流到 150ms 一次。</summary>
        public event Action PhysicalMouseActivity;

        /// <summary>true 时吞掉物理鼠标；显示密码提示界面需要点按钮时置为 false。</summary>
        public bool SuppressMouse = true;

        /// <summary>最近一次物理键鼠活动的时间戳（Environment.TickCount）。</summary>
        public int LastActivityTick;

        public bool IsInstalled { get { return _active; } }

        public InputHook()
        {
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
        }

        public bool Install()
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                // 上一次卸载失败留下的句柄仍在钩子链上，可继续使用；补装可能已摘掉的鼠标钩子
                if (_mouseHook == IntPtr.Zero)
                {
                    try
                    {
                        IntPtr hm = NativeMethods.GetModuleHandle(null);
                        _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, hm, 0);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("InputHook.Install(补装鼠标钩子)", ex);
                    }
                }
                _active = true;
                return true;
            }
            try
            {
                IntPtr module = NativeMethods.GetModuleHandle(null);
                _shiftDown = false;
                _keyboardHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _keyboardProc, module, 0);
                _mouseHook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _mouseProc, module, 0);
                if (_keyboardHook == IntPtr.Zero)
                {
                    Log.Write("安装键盘钩子失败: " + Marshal.GetLastWin32Error());
                    return false;
                }
                if (_mouseHook == IntPtr.Zero)
                    Log.Write("安装鼠标钩子失败（键盘已生效）: " + Marshal.GetLastWin32Error());
                _active = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("InputHook.Install", ex);
                Uninstall();
                return false;
            }
        }

        public void Uninstall()
        {
            // 先关掉拦截开关，再摘钩子。这样即使卸载失败，也不会继续吞键。
            _active = false;
            try
            {
                if (_mouseHook != IntPtr.Zero)
                    _mouseHook = UnhookWithRetry(_mouseHook, "鼠标");
                if (_keyboardHook != IntPtr.Zero)
                    _keyboardHook = UnhookWithRetry(_keyboardHook, "键盘");
            }
            catch (Exception ex)
            {
                Log.Error("InputHook.Uninstall", ex);
            }
            _shiftDown = false;
            LastActivityTick = 0;
        }

        /// <summary>卸载单个钩子，失败重试 3 次；返回剩余句柄（IntPtr.Zero 表示已干净摘除）。</summary>
        private IntPtr UnhookWithRetry(IntPtr hook, string name)
        {
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (NativeMethods.UnhookWindowsHookEx(hook))
                        return IntPtr.Zero;
                    Log.Write(name + "钩子卸载失败（第 " + attempt + " 次尝试），错误码 " + Marshal.GetLastWin32Error());
                }
                catch (Exception ex)
                {
                    Log.Error("InputHook.Unhook " + name, ex);
                }
                System.Threading.Thread.Sleep(40);
            }
            Log.Write(name + "钩子三次卸载均失败，已切换为放行模式（不再吞键），进程退出时由系统回收");
            return hook;
        }

        /// <summary>
        /// 解锁时调用：用注入的方式把所有修饰键、常用键和鼠标键补一个“弹起”。
        /// 锁屏期间这些键的 keyup 会被钩子吞掉（例如按下 Ctrl+Alt+L 时 Ctrl/Alt 的按下已到达系统，
        /// 松开却被吞），系统便认为它们一直按着，表现为解锁后打字全成快捷键、鼠标点击变成 Ctrl+点击。
        /// 注入事件带 INJECTED 标记，永远不被本钩子拦截。
        /// </summary>
        public void ReleaseStuckInput()
        {
            try
            {
                byte[] keys = new byte[]
                {
                    (byte)NativeMethods.VK_LCONTROL, (byte)NativeMethods.VK_RCONTROL,
                    (byte)NativeMethods.VK_LMENU, (byte)NativeMethods.VK_RMENU,
                    (byte)NativeMethods.VK_LSHIFT, (byte)NativeMethods.VK_RSHIFT,
                    (byte)NativeMethods.VK_LWIN, (byte)NativeMethods.VK_RWIN,
                    (byte)NativeMethods.VK_CONTROL, (byte)NativeMethods.VK_MENU, (byte)NativeMethods.VK_SHIFT,
                    (byte)NativeMethods.VK_RETURN, (byte)NativeMethods.VK_SPACE,
                    (byte)NativeMethods.VK_TAB, (byte)NativeMethods.VK_ESCAPE, (byte)NativeMethods.VK_BACK
                };

                foreach (byte vk in keys)
                    NativeMethods.keybd_event(vk, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);

                NativeMethods.mouse_event(
                    NativeMethods.MOUSEEVENTF_LEFTUP | NativeMethods.MOUSEEVENTF_RIGHTUP | NativeMethods.MOUSEEVENTF_MIDDLEUP,
                    0, 0, 0, UIntPtr.Zero);

                _shiftDown = false;
            }
            catch (Exception ex)
            {
                Log.Error("InputHook.ReleaseStuckInput", ex);
            }
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || !_active)
                return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            try
            {
                NativeMethods.KBDLLHOOKSTRUCT data =
                    (NativeMethods.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.KBDLLHOOKSTRUCT));

                // 注入输入放行：SendInput / 自动化脚本 / 远程输入都能通过
                if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0)
                    return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                bool isDown = msg == NativeMethods.WM_KEYDOWN || msg == NativeMethods.WM_SYSKEYDOWN;

                LastActivityTick = Environment.TickCount;

                if (data.vkCode == NativeMethods.VK_LSHIFT || data.vkCode == NativeMethods.VK_RSHIFT)
                {
                    _shiftDown = isDown;
                    return (IntPtr)1;
                }

                if (isDown)
                {
                    Action<Keys, bool> handler = PhysicalKeyDown;
                    if (handler != null) handler((Keys)data.vkCode, _shiftDown);
                }
                return (IntPtr)1;
            }
            catch (Exception ex)
            {
                Log.Error("InputHook.KeyboardCallback", ex);
                return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }
        }

        private IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || !_active)
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            try
            {
                NativeMethods.MSLLHOOKSTRUCT data =
                    (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                // 注入的鼠标（自动化 SendInput）永远放行，保证 UIA 坐标操作不被遮罩吃掉
                if ((data.flags & NativeMethods.LLMHF_INJECTED) != 0)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

                LastActivityTick = Environment.TickCount;

                int now = LastActivityTick;
                if (now - _lastMouseNotifyTick > 150 || now < _lastMouseNotifyTick)
                {
                    _lastMouseNotifyTick = now;
                    Action notify = PhysicalMouseActivity;
                    if (notify != null) notify();
                }

                if (SuppressMouse) return (IntPtr)1; // 纯黑态：吞掉物理鼠标，防误触
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
            catch
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
        }

        public void Dispose()
        {
            Uninstall();
            ReleaseStuckInput();
        }
    }
}
