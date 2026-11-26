using System;

namespace BlackScreenLock
{
    /// <summary>
    /// 显示器开关与系统唤醒状态控制。
    /// 注意：v2 起锁屏用全屏纯黑遮罩代替 DPMS 关屏（关屏再点亮会让 DWM 重建合成表面、
    /// 遮罩丢失一瞬），这里保留的 SC_MONITORPOWER 仅作备用，日常流程不再调用。
    /// 唤醒状态用 SetThreadExecutionState 维持，只影响电源空闲判定，不碰渲染与合成。
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
        /// “始终让 Windows 保持唤醒”，选项默认开启。
        /// 开启后只要本程序在托盘里运行，系统就不会因为空闲进入睡眠/休眠，
        /// 后台自动化不会被打断——显示器仍由电源计划决定是否关闭（更省电）。
        /// </summary>
        private static bool _alwaysAwake = true;

        public static bool AlwaysAwake { get { return _alwaysAwake; } }

        /// <summary>设置并立即应用“保持唤醒”。应在 UI 线程调用（SetThreadExecutionState 是按线程生效的）。</summary>
        public static bool SetAlwaysAwake(bool on)
        {
            _alwaysAwake = on;
            return ApplyExecutionState(0);
        }

        /// <summary>
        /// 锁屏期间阻止系统睡眠，同时让显示器保持点亮（遮罩本身是黑屏，背光本来就要亮着，
        /// 顺带避免电源计划在锁屏中途关屏导致 DWM 重建合成表面）。
        /// </summary>
        public static void KeepSystemAwake()
        {
            SetThreadExecutionStateSafe(NativeMethods.ES_CONTINUOUS
                | NativeMethods.ES_SYSTEM_REQUIRED
                | NativeMethods.ES_DISPLAY_REQUIRED);
        }

        /// <summary>解锁瞬间请求显示器保持点亮，一小段时间后再由调用方恢复正常。</summary>
        public static void RequestDisplayOn()
        {
            SetThreadExecutionStateSafe(NativeMethods.ES_CONTINUOUS
                | NativeMethods.ES_SYSTEM_REQUIRED
                | NativeMethods.ES_DISPLAY_REQUIRED);
        }

        /// <summary>恢复由“保持唤醒”选项决定的状态；选项开启时不能把常驻的唤醒请求清掉。</summary>
        public static void ReleaseExecutionState()
        {
            ApplyExecutionState(0);
        }

        private static bool ApplyExecutionState(uint extra)
        {
            uint flags = NativeMethods.ES_CONTINUOUS;
            if (_alwaysAwake || extra != 0)
                flags |= NativeMethods.ES_SYSTEM_REQUIRED;
            flags |= extra;
            return SetThreadExecutionStateSafe(flags);
        }

        private static bool SetThreadExecutionStateSafe(uint flags)
        {
            try
            {
                return NativeMethods.SetThreadExecutionState(flags) != 0;
            }
            catch (Exception ex)
            {
                Log.Error("DisplayControl.SetThreadExecutionState", ex);
                return false;
            }
        }
    }
}
