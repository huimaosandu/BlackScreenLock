using System;
using System.Threading;
using System.Windows.Forms;

namespace BlackScreenLock
{
    internal static class Program
    {
        private const string MutexName = "Local\\BlackScreenLock.SingleInstance.v1";
        internal const string HiddenWindowTitle = "BlackScreenLock.HiddenWindow";

        [STAThread]
        private static void Main(string[] args)
        {
            bool lockNow = false;
            bool unlockNow = false;
            bool selfTest = false;

            foreach (string raw in args)
            {
                string arg = (raw ?? string.Empty).Trim().ToLowerInvariant();
                if (arg == "--lock" || arg == "-lock" || arg == "/lock") lockNow = true;
                else if (arg == "--unlock" || arg == "-unlock" || arg == "/unlock") unlockNow = true;
                else if (arg == "--selftest" || arg == "-selftest" || arg == "/selftest") selfTest = true;
            }

            if (selfTest)
            {
                Environment.ExitCode = SelfTest.Run();
                return;
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr existing = NativeMethods.FindWindow(null, HiddenWindowTitle);
                    if (existing != IntPtr.Zero && lockNow)
                    {
                        NativeMethods.PostMessage(existing, MessageWindow.WM_APP_TOGGLE, IntPtr.Zero, IntPtr.Zero);
                    }
                    else if (existing != IntPtr.Zero && unlockNow)
                    {
                        NativeMethods.PostMessage(existing, MessageWindow.WM_APP_FORCE_UNLOCK, IntPtr.Zero, IntPtr.Zero);
                    }
                    else if (!lockNow && !unlockNow)
                    {
                        MessageBox.Show(
                            "黑屏锁已经在运行。\n请使用任务栏通知区域的托盘图标操作，或直接按快捷键锁定。",
                            "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { NativeMethods.SetProcessDPIAware(); } catch { }

                App app = new App();
                if (!app.Ready)
                {
                    Log.Write("初始化未完成，进程结束");
                    return;
                }

                Application.Run(app);
                GC.KeepAlive(mutex);
            }
        }
    }
}
