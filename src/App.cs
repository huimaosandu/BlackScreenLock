using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 托盘主控：菜单、全局热键、锁屏控制、各设置窗口的调度。
    /// 整体只有一个隐藏消息窗口 + 一个托盘图标，没有可见主窗体。
    /// </summary>
    internal sealed class App : ApplicationContext
    {
        private const int HotkeyId = 0xA001;

        private readonly Config _config;
        private readonly MessageWindow _messageWindow;
        private readonly LockController _lock;
        private NotifyIcon _tray;
        private ContextMenuStrip _menu;
        private ToolStripMenuItem _lockItem;
        private ToolStripMenuItem _autoStartItem;
        private bool _hotkeyRegistered;
        private bool _ready;

        public bool Ready { get { return _ready; } }

        public App()
        {
            _config = Config.Load();

            // 首次运行：必须先设置解锁密码，之后修改才需要验证原密码
            if (!_config.HasPassword)
            {
                using (PasswordDialog dialog = new PasswordDialog(_config))
                {
                    if (dialog.ShowDialog() != DialogResult.OK)
                    {
                        Log.Write("首次运行未设置密码，程序退出");
                        return;
                    }
                }
                _config.Save();
                Log.Write("首次运行已设置解锁密码");
            }

            _messageWindow = new MessageWindow();
            _messageWindow.HotkeyPressed += OnHotkeyPressed;
            _messageWindow.LockedKeyDown += OnLockedKeyDown;
            _messageWindow.DisplayTurnedOn += OnDisplayTurnedOn;
            _messageWindow.ForceUnlockRequested += OnForceUnlockRequested;
            _messageWindow.RegisterPowerNotification();

            _lock = new LockController(_config, _messageWindow.Handle);
            _lock.Unlocked += OnUnlocked;
            _lock.UnlockFailed += OnUnlockFailed;

            BuildTray();
            ApplyHotkey(_config.HotKeyModifiers, _config.HotKeyVk, false);

            _ready = true;
            Log.Write("程序启动完成，热键=" + KeyMapper.HotkeyText(_config.HotKeyModifiers, _config.HotKeyVk));
        }

        // ---------------- 托盘与菜单 ----------------

        private void BuildTray()
        {
            _menu = new ContextMenuStrip();
            _menu.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            _lockItem = new ToolStripMenuItem("立即锁屏");
            _lockItem.Click += delegate { StartLock(); };
            _menu.Items.Add(_lockItem);

            _menu.Items.Add(new ToolStripSeparator());

            _autoStartItem = new ToolStripMenuItem("开机自动启动");
            _autoStartItem.CheckOnClick = true;
            _autoStartItem.Checked = AutoStart.IsEnabled();
            _autoStartItem.Click += delegate { ToggleAutoStart(); };
            _menu.Items.Add(_autoStartItem);

            ToolStripMenuItem passwordItem = new ToolStripMenuItem("设置密码...");
            passwordItem.Click += delegate { ShowPasswordDialog(); };
            _menu.Items.Add(passwordItem);

            ToolStripMenuItem hotkeyItem = new ToolStripMenuItem("编辑快捷键...");
            hotkeyItem.Click += delegate { ShowHotkeyDialog(); };
            _menu.Items.Add(hotkeyItem);

            ToolStripMenuItem optionsItem = new ToolStripMenuItem("选项...");
            optionsItem.Click += delegate { ShowOptionsDialog(); };
            _menu.Items.Add(optionsItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem helpItem = new ToolStripMenuItem("使用说明");
            helpItem.Click += delegate { ShowHelp(); };
            _menu.Items.Add(helpItem);

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApplication(); };
            _menu.Items.Add(exitItem);

            _tray = new NotifyIcon();
            _tray.Icon = IconFactory.AppIcon;
            _tray.Text = "黑屏锁：Ctrl + Alt + L 锁定屏幕（不影响自动化）";
            _tray.ContextMenuStrip = _menu;
            _tray.Visible = true;
            _tray.DoubleClick += delegate { ShowOptionsDialog(); };

            RefreshMenuText();

            ShowBalloon("黑屏锁已启动",
                "快捷键 " + KeyMapper.HotkeyText(_config.HotKeyModifiers, _config.HotKeyVk) +
                " 锁定屏幕；右键托盘图标可设置密码、快捷键与开机自启。");
        }

        private void RefreshMenuText()
        {
            string text = KeyMapper.HotkeyText(_config.HotKeyModifiers, _config.HotKeyVk);
            if (_lockItem != null)
                _lockItem.ShortcutKeyDisplayString = text;
            if (_tray != null)
                _tray.Text = "黑屏锁：" + text + " 锁定屏幕";
            if (_menu != null)
                _menu.Refresh();
        }

        // ---------------- 热键 ----------------

        private bool ApplyHotkey(int modifiers, int vk, bool persist)
        {
            if (_messageWindow == null) return false;

            int oldModifiers = _config.HotKeyModifiers;
            int oldVk = _config.HotKeyVk;

            UnregisterHotkey();
            bool ok = NativeMethods.RegisterHotKey(
                _messageWindow.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, vk);

            if (!ok)
            {
                Log.Write("注册热键失败: " + KeyMapper.HotkeyText(modifiers, vk) + " err=" + Marshal.GetLastWin32Error());
                // 回滚：尝试恢复旧热键
                if (modifiers != oldModifiers || vk != oldVk)
                    ApplyHotkeyInternal(oldModifiers, oldVk);
                return false;
            }

            _hotkeyRegistered = true;
            _config.HotKeyModifiers = modifiers;
            _config.HotKeyVk = vk;
            if (persist) _config.Save();
            RefreshMenuText();
            return true;
        }

        private void ApplyHotkeyInternal(int modifiers, int vk)
        {
            UnregisterHotkey();
            if (NativeMethods.RegisterHotKey(_messageWindow.Handle, HotkeyId, modifiers | NativeMethods.MOD_NOREPEAT, vk))
            {
                _hotkeyRegistered = true;
                _config.HotKeyModifiers = modifiers;
                _config.HotKeyVk = vk;
                RefreshMenuText();
            }
        }

        private void UnregisterHotkey()
        {
            if (!_hotkeyRegistered) return;
            try
            {
                NativeMethods.UnregisterHotKey(_messageWindow.Handle, HotkeyId);
            }
            catch { }
            _hotkeyRegistered = false;
        }

        // ---------------- 锁屏流程 ----------------

        private void StartLock()
        {
            if (_lock == null || _lock.IsLocked) return;
            try
            {
                ToastForm.ShowToast("正在锁定屏幕", "屏幕即将关闭。解锁方式：直接盲打密码后按回车。", 1500);
                _lock.Lock(1500); // 留出 1.5 秒让提示可见，然后关闭显示器
            }
            catch (Exception ex)
            {
                Log.Error("App.StartLock", ex);
            }
        }

        private void OnHotkeyPressed()
        {
            StartLock();
        }

        private void OnLockedKeyDown(Keys key, bool shift)
        {
            if (_lock != null) _lock.ProcessKey(key, shift);
        }

        private void OnDisplayTurnedOn()
        {
            // 锁屏期间显示器被意外点亮 -> 立即熄灭
            if (_lock != null && _lock.IsLocked)
                DisplayControl.MonitorOff();
        }

        private void OnUnlocked()
        {
            ToastForm.ShowToast("已解锁", "屏幕已恢复，可以正常操作。", 2000);
        }

        private void OnUnlockFailed(string reason)
        {
            Log.Write("解锁失败：" + reason);
        }

        /// <summary>供命令行 --unlock 调用：跳过密码直接解锁（等同本地强制解除）。</summary>
        private void OnForceUnlockRequested()
        {
            Log.Write("收到命令行的强制解锁请求");
            if (_lock != null && _lock.IsLocked) _lock.Unlock();
        }

        // ---------------- 菜单动作 ----------------

        private void ToggleAutoStart()
        {
            bool wanted = _autoStartItem.Checked;
            bool ok = AutoStart.SetEnabled(wanted);
            if (!ok)
            {
                _autoStartItem.Checked = AutoStart.IsEnabled();
                MessageBox.Show("写入注册表失败，开机自启动未能" + (wanted ? "开启" : "关闭") + "。\n可尝试以管理员身份运行，或使用系统的启动文件夹方式。",
                    "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            _autoStartItem.Checked = AutoStart.IsEnabled();
            ShowBalloon("开机自启动已" + (wanted ? "开启" : "关闭"),
                wanted ? "下次开机将静默启动并驻留在通知区域（任务栏“显示隐藏的图标”）。" : "已从系统启动项中移除。");
        }

        private void ShowPasswordDialog()
        {
            using (PasswordDialog dialog = new PasswordDialog(_config))
            {
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    if (_config.Save())
                        ShowBalloon("密码已更新", "新的解锁密码已生效。");
                    else
                        MessageBox.Show("密码已修改但写入配置文件失败，重启后恢复旧密码。请检查配置文件目录权限。",
                            "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ShowHotkeyDialog()
        {
            using (HotkeyDialog dialog = new HotkeyDialog(_config.HotKeyModifiers, _config.HotKeyVk))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                if (!dialog.Changed) return;

                if (ApplyHotkey(dialog.ResultModifiers, dialog.ResultVk, true))
                {
                    ShowBalloon("快捷键已更新", "现在是 " + KeyMapper.HotkeyText(dialog.ResultModifiers, dialog.ResultVk) + "。");
                }
                else
                {
                    MessageBox.Show("该快捷键已被其他程序占用，注册失败，已保留原来的快捷键。\n请换一个组合键再试。",
                        "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
        }

        private void ShowOptionsDialog()
        {
            using (OptionsDialog dialog = new OptionsDialog(_config, AutoStart.IsEnabled()))
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;

                bool autoOk = AutoStart.SetEnabled(dialog.AutoStartEnabled);
                _config.SoundOnError = dialog.SoundOnError;
                _config.KeepDisplayOff = dialog.KeepDisplayOff;
                bool saveOk = _config.Save();

                if (_autoStartItem != null)
                    _autoStartItem.Checked = AutoStart.IsEnabled();

                if (!autoOk)
                    MessageBox.Show("写入开机自启动注册表失败。", "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (!saveOk)
                    MessageBox.Show("选项已应用，但配置文件写入失败，重启后会丢失。", "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowHelp()
        {
            string exePath = AutoStart.ExecutablePath;
            string text =
                "黑屏锁 v1.0\r\n\r\n" +
                "【锁定】按 " + KeyMapper.HotkeyText(_config.HotKeyModifiers, _config.HotKeyVk) + "（可在右键菜单修改）\r\n" +
                "　　　屏幕立刻黑屏，物理键鼠被临时屏蔽，Windows 会话保持解锁状态。\r\n\r\n" +
                "【解锁】直接盲打密码，按回车。密码正确后屏幕点亮。\r\n" +
                "　　　按 Esc 清空已输入的内容；输错会播放提示音（可在选项中关闭）。\r\n\r\n" +
                "【为什么不影响自动化】锁屏只关闭显示器背光（DPMS），桌面会话与窗口渲染完全没变：\r\n" +
                "　　　· UI Automation 元素树、Pattern 调用完全可用；\r\n" +
                "　　　· 截屏（BitBlt / PrintWindow / 图形捕获）仍能取到正常画面；\r\n" +
                "　　　· 程序注入的键鼠（SendInput）不会被拦截，只有物理键鼠会被拦截。\r\n\r\n" +
                "【注意】Ctrl + Alt + Del 属于系统安全序列，任何程序都拦不住，\r\n" +
                "　　　按下会点亮屏幕并进入安全桌面。\r\n\r\n" +
                "程序位置：" + exePath;

            MessageBox.Show(text, "黑屏锁 - 使用说明", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                _tray.ShowBalloonTip(3000, title, text, ToolTipIcon.Info);
            }
            catch { }
        }

        // ---------------- 退出 ----------------

        private void ExitApplication()
        {
            try
            {
                if (_lock != null && _lock.IsLocked)
                {
                    // 避免退出后把屏幕留在黑屏状态
                    _lock.Unlock();
                }
            }
            catch { }

            UnregisterHotkey();
            ToastForm.HideCurrent();

            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
            if (_messageWindow != null)
                _messageWindow.Destroy();
            if (_lock != null)
            {
                _lock.Dispose();
            }

            Log.Write("程序退出");
            ExitThread();
        }
    }
}
