using System;

namespace BlackScreenLock
{
    /// <summary>
    /// 显示器开关与电源状态控制。
    /// 这里用的是 SC_MONITORPOWER（DPMS）方案：只让显示器进入断电/待机，
    /// 桌面会话、DWM 合成、窗口渲染全部保持原状 —— 这是本工具“不影响 UIA、不影响截图”的根本原因。
    /// </summary>
    internal static class DisplayControl
    {
        public static void MonitorOff()
        {
            SendMonitorPower(NativeMethods.MONITOR_OFF);
        }

        public static void MonitorOn()
        {
            SendMonitorPower(NativeMethods.MONITOR_ON);
        }

        private static void SendMonitorPower(int state)
        {
            try
            {
                NativeMethods.SendMessage(
                    NativeMethods.HWND_BROADCAST,
                    NativeMethods.WM_SYSCOMMAND,
                    (IntPtr)NativeMethods.SC_MONITORPOWER,
                    (IntPtr)state);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayControl.SendMonitorPower(" + state + ")", ex);
            }
        }

        /// <summary>
        /// 锁屏期间阻止系统进入睡眠（但不禁用关屏），保证自动化任务不会因为休眠中断。
        /// 仅设置 ES_SYSTEM_REQUIRED，绝不设置 ES_DISPLAY_REQUIRED。
        /// </summary>
        public static void KeepSystemAwake()
        {
            try
            {
                NativeMethods.SetThreadExecutionState(
                    NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayControl.KeepSystemAwake", ex);
            }
        }

        /// <summary>解锁瞬间请求显示器保持点亮，一小段时间后再由调用方恢复正常。</summary>
        public static void RequestDisplayOn()
        {
            try
            {
                NativeMethods.SetThreadExecutionState(
                    NativeMethods.ES_CONTINUOUS | NativeMethods.ES_SYSTEM_REQUIRED | 0x00000002u /* ES_DISPLAY_REQUIRED */);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayControl.RequestDisplayOn", ex);
            }
        }

        public static void ReleaseExecutionState()
        {
            try
            {
                NativeMethods.SetThreadExecutionState(NativeMethods.ES_CONTINUOUS);
            }
            catch (Exception ex)
            {
                Log.Error("DisplayControl.ReleaseExecutionState", ex);
            }
        }
    }
}
