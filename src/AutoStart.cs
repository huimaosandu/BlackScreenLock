using System;
using System.IO;
using Microsoft.Win32;

namespace BlackScreenLock
{
    /// <summary>
    /// 开机自启动：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run（用户级，免管理员权限）。
    /// 启动参数使用 --tray，保证开机后静默驻留托盘、不弹任何窗口。
    /// </summary>
    internal static class AutoStart
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "BlackScreenLock";

        public static string ExecutablePath
        {
            get { return Path.GetFullPath(System.Windows.Forms.Application.ExecutablePath); }
        }

        private static string BuildCommand()
        {
            return "\"" + ExecutablePath + "\" --tray";
        }

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object value = key.GetValue(ValueName);
                    if (value == null) return false;
                    string cmd = value.ToString();
                    if (string.IsNullOrEmpty(cmd)) return false;
                    return cmd.IndexOf(ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch (Exception ex)
            {
                Log.Error("AutoStart.IsEnabled", ex);
                return false;
            }
        }

        public static bool SetEnabled(bool enabled)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null) return false;
                    if (enabled)
                    {
                        key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
                        Log.Write("已写入开机自启动: " + BuildCommand());
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                            key.DeleteValue(ValueName, false);
                        Log.Write("已移除开机自启动");
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("AutoStart.SetEnabled", ex);
                return false;
            }
        }
    }
}
