using System;
using System.IO;
using System.Text;

namespace BlackScreenLock
{
    /// <summary>
    /// 极简日志：写入 exe 目录 logs\yyyy-MM-dd.log，不可写则落到 %TEMP%。
    /// 单文件超过 2MB 时自动截断，避免长期挂机把磁盘写满。
    /// </summary>
    internal static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static bool _resolved;

        private static string ResolvePath()
        {
            if (_resolved) return _path;
            lock (Sync)
            {
                if (_resolved) return _path;
                string[] dirs = new string[]
                {
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs"),
                    Path.Combine(Path.GetTempPath(), "BlackScreenLock_logs")
                };
                foreach (string dir in dirs)
                {
                    try
                    {
                        Directory.CreateDirectory(dir);
                        string probe = Path.Combine(dir, ".probe_" + Guid.NewGuid().ToString("N"));
                        File.WriteAllText(probe, "x");
                        File.Delete(probe);
                        _path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                        _resolved = true;
                        return _path;
                    }
                    catch { }
                }
                _path = null;
                _resolved = true;
                return null;
            }
        }

        public static void Write(string message)
        {
            try
            {
                string path = ResolvePath();
                if (path == null) return;
                lock (Sync)
                {
                    try
                    {
                        FileInfo fi = new FileInfo(path);
                        if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                            File.WriteAllText(path, string.Empty, Encoding.UTF8);
                    }
                    catch { }
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[ERROR] " + where + " -> " + (ex == null ? "null" : ex.GetType().Name + ": " + ex.Message));
            if (ex != null && ex.StackTrace != null)
                Write("        " + ex.StackTrace.Replace(Environment.NewLine, Environment.NewLine + "        "));
        }
    }
}
