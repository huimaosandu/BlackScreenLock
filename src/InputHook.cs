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

        /// <summary>物理按键按下（虚拟键, Shift 是否按下）。</summary>
        public event Action<Keys, bool> PhysicalKeyDown;

        public bool IsInstalled { get { return _keyboardHook != IntPtr.Zero; } }

        public InputHook()
        {
            _keyboardProc = KeyboardCallback;
            _mouseProc = MouseCallback;
        }

        public bool Install()
        {
            if (_keyboardHook != IntPtr.Zero) return true;
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
            try
            {
                if (_mouseHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_mouseHook);
                    _mouseHook = IntPtr.Zero;
                }
                if (_keyboardHook != IntPtr.Zero)
                {
                    NativeMethods.UnhookWindowsHookEx(_keyboardHook);
                    _keyboardHook = IntPtr.Zero;
                }
            }
            catch (Exception ex)
            {
                Log.Error("InputHook.Uninstall", ex);
            }
            _shiftDown = false;
        }

        private IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0)
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
            if (nCode < 0)
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            try
            {
                NativeMethods.MSLLHOOKSTRUCT data =
                    (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));
                if ((data.flags & NativeMethods.LLMHF_INJECTED) != 0)
                    return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
                return (IntPtr)1; // 物理鼠标事件全部吞掉
            }
            catch
            {
                return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
        }

        public void Dispose()
        {
            Uninstall();
        }
    }
}
