using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace BlackScreenLock
{
    /// <summary>
    /// 绿色配置文件。优先放在 exe 同目录（免安装、可随 U 盘携带）；
    /// 若该目录不可写（如放在 Program Files），自动回落到 %APPDATA%\BlackScreenLock。
    /// 密码只保存 PBKDF2 派生结果，不保存明文。
    /// </summary>
    internal sealed class Config
    {
        public const string FileName = "BlackScreenLock.cfg";
        private const int Iterations = 100000;
        private const string DefaultPassword = "";

        private readonly string _path;

        public byte[] Salt;
        public byte[] Hash;
        public bool SoundOnError = true;
        public bool KeepDisplayOff = true;
        /// <summary>始终阻止系统睡眠（默认开启）：只要程序在托盘运行，Windows 不会空闲睡眠/休眠。</summary>
        public bool AlwaysAwake = true;

        /// <summary>全局热键：修饰键位（MOD_CONTROL|MOD_ALT 等）</summary>
        public int HotKeyModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
        /// <summary>全局热键：虚拟键码</summary>
        public int HotKeyVk = NativeMethods.VK_L;

        public string FilePath { get { return _path; } }
        public bool HasPassword { get { return Salt != null && Salt.Length > 0 && Hash != null && Hash.Length > 0; } }

        private Config(string path)
        {
            _path = path;
        }

        public static string ResolvePath()
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string candidate = Path.Combine(exeDir, FileName);
            if (File.Exists(candidate))
                return candidate;

            // 目录是否可写
            try
            {
                string probe = Path.Combine(exeDir, ".write_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
                return candidate;
            }
            catch
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlackScreenLock");
                try { Directory.CreateDirectory(dir); } catch { }
                return Path.Combine(dir, FileName);
            }
        }

        public static Config Load()
        {
            Config cfg = new Config(ResolvePath());
            try
            {
                if (File.Exists(cfg._path))
                {
                    foreach (string raw in File.ReadAllLines(cfg._path, Encoding.UTF8))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                            continue;
                        int idx = line.IndexOf('=');
                        if (idx <= 0) continue;
                        string key = line.Substring(0, idx).Trim();
                        string val = line.Substring(idx + 1).Trim();
                        switch (key.ToLowerInvariant())
                        {
                            case "salt": cfg.Salt = HexToBytes(val); break;
                            case "hash": cfg.Hash = HexToBytes(val); break;
                            case "soundonerror": cfg.SoundOnError = val != "0"; break;
                            case "keepdisplayoff": cfg.KeepDisplayOff = val != "0"; break;
                            case "alwaysawake": cfg.AlwaysAwake = val != "0"; break;
                            case "hotkeymodifiers": cfg.HotKeyModifiers = ParseInt(val, cfg.HotKeyModifiers); break;
                            case "hotkeyvk": cfg.HotKeyVk = ParseInt(val, cfg.HotKeyVk); break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("读取配置失败: " + ex.Message);
            }
            return cfg;
        }

        public bool Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# BlackScreenLock 配置，删除本文件将重置为初始状态");
                sb.AppendLine("Salt=" + BytesToHex(Salt));
                sb.AppendLine("Hash=" + BytesToHex(Hash));
                sb.AppendLine("SoundOnError=" + (SoundOnError ? "1" : "0"));
                sb.AppendLine("KeepDisplayOff=" + (KeepDisplayOff ? "1" : "0"));
                sb.AppendLine("AlwaysAwake=" + (AlwaysAwake ? "1" : "0"));
                sb.AppendLine("HotKeyModifiers=" + HotKeyModifiers.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("HotKeyVk=" + HotKeyVk.ToString(CultureInfo.InvariantCulture));
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_path, sb.ToString(), Encoding.UTF8);
                return true;
            }
            catch (Exception ex)
            {
                Log.Write("保存配置失败: " + ex.Message);
                return false;
            }
        }

        public void SetPassword(string password)
        {
            byte[] salt = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(salt);
            }
            Salt = salt;
            Hash = Derive(password, salt);
        }

        public bool VerifyPassword(string password)
        {
            if (!HasPassword) return password == DefaultPassword;

            // 盲打时最容易出错的就是大小写（CapsLock 状态、Shift 是否按住）。
            // 先按原样比对；不通过时再按小写规范化比对一次，兼容历史配置，不区分字母大小写。
            if (Match(password)) return true;
            string lowered = (password ?? string.Empty).ToLowerInvariant();
            if (lowered != password) return Match(lowered);
            return false;
        }

        private bool Match(string password)
        {
            if (Salt == null || Hash == null) return false;
            byte[] test = Derive(password, Salt);
            if (test.Length != Hash.Length) return false;
            // 定长比较，避免明显的时序差异
            int diff = 0;
            for (int i = 0; i < test.Length; i++)
                diff |= (test[i] ^ Hash[i]);
            return diff == 0;
        }

        private static byte[] Derive(string password, byte[] salt)
        {
            using (Rfc2898DeriveBytes kdf = new Rfc2898DeriveBytes(password ?? string.Empty, salt, Iterations))
            {
                return kdf.GetBytes(32);
            }
        }

        private static string BytesToHex(byte[] data)
        {
            if (data == null) return string.Empty;
            StringBuilder sb = new StringBuilder(data.Length * 2);
            foreach (byte b in data)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static int ParseInt(string text, int fallback)
        {
            try
            {
                return int.Parse(text, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.Trim();
            if (hex.Length % 2 != 0) return null;
            try
            {
                byte[] result = new byte[hex.Length / 2];
                for (int i = 0; i < result.Length; i++)
                    result[i] = byte.Parse(hex.Substring(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                return result;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>保存后立即生效，供调用方刷新内存中的选项。</summary>
        public void CopyTogglesFrom(Config other)
        {
            SoundOnError = other.SoundOnError;
            KeepDisplayOff = other.KeepDisplayOff;
            AlwaysAwake = other.AlwaysAwake;
            HotKeyModifiers = other.HotKeyModifiers;
            HotKeyVk = other.HotKeyVk;
        }
    }
}
