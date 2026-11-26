using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BlackScreenLock
{
    /// <summary>
    /// 自检：验证配置、密钥派生、图标绘制、钩子安装、全局热键、电源通知等非破坏性能力。
    /// 不会真的关闭显示器，也不会写用户的配置文件与注册表。
    /// 结果写入 exe 目录的 selftest.log。
    /// </summary>
    internal static class SelfTest
    {
        public static int Run()
        {
            StringBuilder log = new StringBuilder();
            int failed = 0;

            log.AppendLine("BlackScreenLock 自检报告");
            log.AppendLine("时间: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            log.AppendLine("系统: " + Environment.OSVersion);
            log.AppendLine("CLR : " + Environment.Version);
            log.AppendLine("路径: " + AppDomain.CurrentDomain.BaseDirectory);
            log.AppendLine(new string('-', 72));

            // 1) 密码派生与校验
            try
            {
                Config config = Config.Load();
                bool hadPassword = config.HasPassword;
                config.SetPassword("Selftest#123");
                bool rightOk = config.VerifyPassword("Selftest#123");
                bool wrongOk = !config.VerifyPassword("Selftest#124");
                Check(log, ref failed, "密码派生与校验",
                    rightOk && wrongOk,
                    "已存在密码=" + hadPassword + " 正码通过=" + rightOk + " 误码拒绝=" + wrongOk + "（仅在内存中验证，未写入磁盘）");
                config.HotKeyModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
                config.HotKeyVk = NativeMethods.VK_L;
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "密码派生与校验", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 1b) 锁屏输入链路：字符缓冲 -> 退格 -> 回车提交 -> 密码校验
            try
            {
                Config cfg = Config.Load();
                cfg.SetPassword("Abc123");
                cfg.DimBacklight = false; // 自检不走真实调暗路径（Esc 测试会触发 BackToIdle）
                LockOverlayForm overlay = new LockOverlayForm();
                LockController controller = new LockController(cfg, IntPtr.Zero, overlay);
                bool okRight = TypePassword(controller, "Abc123", 0);
                bool okWrong = !TypePassword(controller, "Abc124", 0);
                bool okBack = TypePassword(controller, "Abc123X", 1);

                // Esc 快捷路径（真实 ProcessKey 链路）：有输入时只清空、保持磨砂界面；空缓冲再按才返回纯黑
                controller.SelfTestBeginPrompt();
                controller.ProcessKey(Keys.A, false);      // 输入一个字符
                controller.ProcessKey(Keys.Escape, false); // 第一次 Esc：只清空，磨砂界面应保留
                bool esc1KeptPrompt = controller.IsLocked && controller.IsPromptVisible;
                controller.ProcessKey(Keys.Escape, false); // 第二次 Esc：返回纯黑（仍是锁定态）
                bool esc2BackToIdle = controller.IsLocked && !controller.IsPromptVisible;

                controller.Dispose();
                overlay.Dispose();
                Check(log, ref failed, "锁屏输入与密码校验", okRight && okWrong && okBack,
                    "正确序列=" + okRight + " 错误序列被拒=" + okWrong + " 退格后可解锁=" + okBack);
                Check(log, ref failed, "Esc 清空/返回黑屏", esc1KeptPrompt && esc2BackToIdle,
                    "有输入按 Esc 保持磨砂=" + esc1KeptPrompt + "，空缓冲按 Esc 返回纯黑=" + esc2BackToIdle);
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "锁屏输入与密码校验", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 1c) 遮罩层：截图排除（WDA）+ 提示界面预览导出
            try
            {
                LockOverlayForm overlay = new LockOverlayForm();
                bool excluded = overlay.CaptureExcluded;
                string preview = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui-preview.png");
                overlay.ExportPreview(preview);
                bool previewOk = File.Exists(preview) && new FileInfo(preview).Length > 0;
                overlay.Dispose();
                Check(log, ref failed, "遮罩截图排除 (WDA)", excluded,
                    excluded ? "SetWindowDisplayAffinity 成功：遮罩不会出现在截图里" : "设置失败，遮罩可能被截屏拍到");
                Check(log, ref failed, "提示界面预览导出", previewOk,
                    previewOk ? "已导出 " + preview : "导出失败");
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "遮罩截图排除 (WDA)", false, ex.GetType().Name + ": " + ex.Message);
            }

            // 2) 按键映射
            try
            {
                bool ok = KeyMapper.ToChar(Keys.A, false) == 'a'
                       && KeyMapper.ToChar(Keys.A, true) == 'A'
                       && KeyMapper.ToChar(Keys.D0, false) == '0'
                       && KeyMapper.ToChar(Keys.D0, true) == ')'
                       && KeyMapper.ToChar(Keys.OemMinus, true) == '_'
                       && KeyMapper.ToChar(Keys.F5, false) == '\0';
                Check(log, ref failed, "按键到字符映射", ok, "字母大小写/数字符号/退格键均按预期");
                Check(log, ref failed, "热键文本", KeyMapper.HotkeyText(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_L) == "Ctrl + Alt + L",
                    "当前文本: " + KeyMapper.HotkeyText(NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, NativeMethods.VK_L));
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "按键到字符映射", false, ex.Message);
            }

            // 3) 图标绘制
            try
            {
                string png = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon-preview.png");
                IconFactory.ExportPng(png, 64);
                bool ok = File.Exists(png) && new FileInfo(png).Length > 0;
                Check(log, ref failed, "图标绘制", ok, ok ? "已导出 " + png : "导出失败");
                Icon icon = IconFactory.AppIcon;
                Check(log, ref failed, "托盘图标句柄", icon != null && icon.Width > 0, icon == null ? "空" : icon.Width + "x" + icon.Height);
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "图标绘制", false, ex.Message);
            }

            // 4) 键鼠钩子
            try
            {
                InputHook hook = new InputHook();
                bool installed = hook.Install();
                hook.Uninstall();
                Check(log, ref failed, "低级键鼠钩子安装/卸载", installed, installed ? "钩子已临时安装并成功卸载" : "安装失败，可能被安全软件拦截");
                hook.Dispose();
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "低级键鼠钩子安装/卸载", false, ex.Message);
            }

            // 5) 隐藏消息窗口 + 全局热键 + 电源通知
            MessageWindow window = null;
            try
            {
                window = new MessageWindow();
                Check(log, ref failed, "隐藏消息窗口创建", window.Handle != IntPtr.Zero, "hwnd=" + window.Handle);

                bool hotkeyOk = NativeMethods.RegisterHotKey(
                    window.Handle, 0x5A5A,
                    NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_NOREPEAT,
                    NativeMethods.VK_L);
                if (hotkeyOk) NativeMethods.UnregisterHotKey(window.Handle, 0x5A5A);
                Check(log, ref failed, "全局热键注册 Ctrl+Alt+L", hotkeyOk, hotkeyOk ? "注册与注销成功" : "被其他程序占用（运行时请改用其他组合键）");

                bool powerOk = window.RegisterPowerNotification();
                Check(log, ref failed, "显示器电源通知注册", powerOk, powerOk ? "可感知显示器被点亮并自动重新熄灭" : "注册失败（强制保持黑屏改为定时兜底）");
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "隐藏消息窗口 / 热键", false, ex.Message);
            }
            finally
            {
                if (window != null) window.Destroy();
            }

            // 5b) “保持系统唤醒”执行状态可开可关（只影响本自检进程，退出即自动释放）
            try
            {
                Config cfgProbe = Config.Load();
                bool onOk = DisplayControl.SetAlwaysAwake(true);
                bool offOk = DisplayControl.SetAlwaysAwake(false);
                DisplayControl.SetAlwaysAwake(cfgProbe.AlwaysAwake);
                Check(log, ref failed, "保持系统唤醒执行状态", onOk && offOk,
                    "开/关均可调用，已恢复为配置值（" + (cfgProbe.AlwaysAwake ? "默认开启" : "关闭") + "）");
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "保持系统唤醒执行状态", false, ex.Message);
            }

            // 5c) 亮度调节支持探测（只读：不改任何亮度，不写状态文件）
            try
            {
                bool dimmed = DisplayBrightness.IsDimmed;
                string support = DisplayBrightness.ProbeSupport();
                Check(log, ref failed, "背光亮度通道探测（只读）", true,
                    (dimmed ? "警告：自检期间检测到调暗状态，" : string.Empty) + support);
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "背光亮度通道探测（只读）", false, ex.Message);
            }

            // 6) 开机自启动项可读取（不写入）
            try
            {
                bool enabled = AutoStart.IsEnabled();
                Check(log, ref failed, "读取开机自启动状态", true,
                    enabled ? "当前已开启（指向 " + AutoStart.ExecutablePath + "）" : "当前未开启（可在托盘右键菜单开启）");
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "读取开机自启动状态", false, ex.Message);
            }

            // 6b) 开机自启动注册表写入/读回/删除往返（独立测试值名，不触碰真实启动项）
            try
            {
                const string probeName = "BlackScreenLock_SelfTest";
                using (RegistryKey runKey = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run"))
                {
                    runKey.SetValue(probeName, "\"C:\\selftest.exe\" --tray", RegistryValueKind.String);
                    object back = runKey.GetValue(probeName);
                    runKey.DeleteValue(probeName, false);
                    bool cleared = runKey.GetValue(probeName) == null;
                    bool ok = back != null && back.ToString().IndexOf("--tray", StringComparison.Ordinal) >= 0 && cleared;
                    Check(log, ref failed, "开机自启动注册表读写", ok,
                        ok ? "写入/读回/删除均成功" : "往返失败（可能缺少 HKCU 写权限）");
                }
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "开机自启动注册表读写", false, ex.Message);
            }

            // 7) 配置文件路径
            try
            {
                Config probe = Config.Load();
                Check(log, ref failed, "配置文件路径", probe.FilePath != null, probe.FilePath);
            }
            catch (Exception ex)
            {
                Check(log, ref failed, "配置文件路径", false, ex.Message);
            }

            log.AppendLine(new string('-', 72));
            log.AppendLine(failed == 0 ? "全部通过。" : ("失败项: " + failed));

            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"), log.ToString(), Encoding.UTF8);
            }
            catch { }

            return failed == 0 ? 0 : 1;
        }

        private static void Check(StringBuilder log, ref int failed, string name, bool ok, string detail)
        {
            log.AppendLine((ok ? "[PASS] " : "[FAIL] ") + name + "  ->  " + (detail ?? string.Empty));
            if (!ok) failed++;
        }

        /// <summary>模拟在锁屏状态下键入一串字符，末尾按回车，返回是否解锁成功。</summary>
        private static bool TypePassword(LockController controller, string text, int backCount)
        {
            controller.SelfTestReset();
            foreach (char c in text)
            {
                Keys key;
                bool shift;
                if (!KeyCharMap(c, out key, out shift)) return false;
                controller.SelfTestKey(key, shift);
            }
            for (int i = 0; i < backCount; i++)
                controller.SelfTestKey(Keys.Back, false);
            return controller.SelfTestKey(Keys.Enter, false);
        }

        private static bool KeyCharMap(char c, out Keys key, out bool shift)
        {
            if (c >= 'a' && c <= 'z') { key = (Keys)((int)Keys.A + (c - 'a')); shift = false; return true; }
            if (c >= 'A' && c <= 'Z') { key = (Keys)((int)Keys.A + (c - 'A')); shift = true; return true; }
            if (c >= '0' && c <= '9') { key = (Keys)((int)Keys.D0 + (c - '0')); shift = false; return true; }
            key = Keys.None;
            shift = false;
            return false;
        }
    }
}
