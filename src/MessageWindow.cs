using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 隐藏消息窗口：接收全局热键、电源广播（显示器开关状态）以及钩子转发过来的按键。
    /// 不显示、不抢焦点、不进任务栏与 Alt+Tab，因此对 UIA 的元素树与截图都没有任何遮挡。
    /// </summary>
    internal sealed class MessageWindow : NativeWindow
    {
        public const int WM_APP_KEY = 0x8000 + 200;      // wParam=vk, lParam=shift
        public const int WM_APP_TOGGLE = 0x8000 + 201;
        public const int WM_APP_FORCE_UNLOCK = 0x8000 + 202;
        public const int WM_APP_MOUSE = 0x8000 + 203;

        public event Action<Keys, bool> LockedKeyDown;
        public event Action HotkeyPressed;
        public event Action DisplayTurnedOn;
        public event Action ForceUnlockRequested;
        public event Action PhysicalMouseActivity;

        private IntPtr _powerNotification = IntPtr.Zero;

        public MessageWindow()
        {
            CreateParams cp = new CreateParams();
            cp.Caption = "BlackScreenLock.HiddenWindow";
            cp.X = -32000;
            cp.Y = -32000;
            cp.Width = 1;
            cp.Height = 1;
            cp.Style = 0;
            cp.ExStyle = 0x00000080; // WS_EX_TOOLWINDOW
            CreateHandle(cp);
        }

        public bool RegisterPowerNotification()
        {
            try
            {
                Guid guid = NativeMethods.GUID_CONSOLE_DISPLAY_STATE;
                _powerNotification = NativeMethods.RegisterPowerSettingNotification(
                    Handle, ref guid, NativeMethods.DEVICE_NOTIFY_WINDOW_HANDLE);
                if (_powerNotification == IntPtr.Zero)
                {
                    Log.Write("注册显示器电源通知失败: " + Marshal.GetLastWin32Error());
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("MessageWindow.RegisterPowerNotification", ex);
                return false;
            }
        }

        public void UnregisterPowerNotification()
        {
            try
            {
                if (_powerNotification != IntPtr.Zero)
                {
                    NativeMethods.UnregisterPowerSettingNotification(_powerNotification);
                    _powerNotification = IntPtr.Zero;
                }
            }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case NativeMethods.WM_HOTKEY:
                    if (HotkeyPressed != null) HotkeyPressed();
                    break;

                case NativeMethods.WM_POWERBROADCAST:
                    HandlePowerBroadcast(m);
                    break;

                case WM_APP_KEY:
                    {
                        Action<Keys, bool> handler = LockedKeyDown;
                        if (handler != null)
                            handler((Keys)m.WParam.ToInt32(), m.LParam.ToInt32() != 0);
                        break;
                    }

                case WM_APP_TOGGLE:
                    if (HotkeyPressed != null) HotkeyPressed();
                    break;

                case WM_APP_FORCE_UNLOCK:
                    {
                        Action handler = ForceUnlockRequested;
                        if (handler != null) handler();
                        break;
                    }

                case WM_APP_MOUSE:
                    {
                        Action handler = PhysicalMouseActivity;
                        if (handler != null) handler();
                        break;
                    }
            }
            base.WndProc(ref m);
        }

        private void HandlePowerBroadcast(Message m)
        {
            if (m.WParam.ToInt32() != NativeMethods.PBT_POWERSETTINGCHANGE) return;
            try
            {
                NativeMethods.POWERBROADCAST_SETTING setting =
                    (NativeMethods.POWERBROADCAST_SETTING)Marshal.PtrToStructure(
                        m.LParam, typeof(NativeMethods.POWERBROADCAST_SETTING));

                if (setting.PowerSetting != NativeMethods.GUID_CONSOLE_DISPLAY_STATE) return;
                // Data: 0=关闭 1=开启 2=变暗
                if (setting.Data == 1)
                {
                    Action handler = DisplayTurnedOn;
                    if (handler != null) handler();
                }
            }
            catch (Exception ex)
            {
                Log.Error("MessageWindow.HandlePowerBroadcast", ex);
            }
        }

        public void Destroy()
        {
            UnregisterPowerNotification();
            try { DestroyHandle(); } catch { }
        }
    }
}
