using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 高级选项窗口：开机自启动、错误提示音、保持系统唤醒。
    /// 只收集用户选择，写注册表与保存配置由调用方统一执行。
    /// </summary>
    internal sealed class OptionsDialog : Form
    {
        private readonly Config _config;
        private CheckBox _autoStart;
        private CheckBox _sound;
        private CheckBox _awake;

        public bool AutoStartEnabled { get { return _autoStart.Checked; } }
        public bool SoundOnError { get { return _sound.Checked; } }
        /// <summary>始终阻止系统睡眠，默认勾选。</summary>
        public bool AlwaysAwake { get { return _awake.Checked; } }
        /// <summary>锁屏已改为纯黑遮罩代替关屏，不再提供“强制保持黑屏”开关。</summary>
        public bool KeepDisplayOff { get { return false; } }

        public OptionsDialog(Config config, bool autoStartEnabled)
        {
            _config = config;
            BuildUi(autoStartEnabled);
        }

        private void BuildUi(bool autoStartEnabled)
        {
            Font uiFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            Font tipFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);

            Text = "选项";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(420, 352);
            Icon = IconFactory.AppIcon;
            Font = uiFont;
            BackColor = Color.FromArgb(250, 250, 252);

            _autoStart = new CheckBox();
            _autoStart.Text = "开机自动启动（登录后在托盘常驻）";
            _autoStart.Location = new Point(18, 16);
            _autoStart.Size = new Size(384, 24);
            _autoStart.Font = uiFont;
            _autoStart.Checked = autoStartEnabled;
            Controls.Add(_autoStart);

            Label autoTip = new Label();
            autoTip.Location = new Point(38, 42);
            autoTip.Size = new Size(360, 34);
            autoTip.Font = tipFont;
            autoTip.ForeColor = Color.FromArgb(120, 120, 130);
            autoTip.Text = "写入当前用户注册表启动项，不需要管理员权限。开机后不弹窗口，只在通知区域\r\n（任务栏“显示隐藏的图标”）出现托盘图标，随时待命。";
            Controls.Add(autoTip);

            _sound = new CheckBox();
            _sound.Text = "密码错误时播放提示音";
            _sound.Location = new Point(18, 84);
            _sound.Size = new Size(300, 24);
            _sound.Font = uiFont;
            _sound.Checked = _config != null && _config.SoundOnError;
            Controls.Add(_sound);

            _awake = new CheckBox();
            _awake.Text = "让 Windows 保持唤醒（不进入睡眠 / 休眠）";
            _awake.Location = new Point(18, 114);
            _awake.Size = new Size(384, 24);
            _awake.Font = uiFont;
            _awake.Checked = _config == null || _config.AlwaysAwake;
            Controls.Add(_awake);

            Label awakeTip = new Label();
            awakeTip.Location = new Point(38, 140);
            awakeTip.Size = new Size(364, 34);
            awakeTip.Font = tipFont;
            awakeTip.ForeColor = Color.FromArgb(120, 120, 130);
            awakeTip.Text = "默认开启。开启后只要本程序在托盘运行，后台自动化不会因系统睡眠而中断；\r\n" +
                            "显示器仍按电源计划关闭以省电（要让屏幕也常亮，请在 Windows 电源设置里选“从不关闭显示器”）。";
            Controls.Add(awakeTip);

            Label keepNote = new Label();
            keepNote.Location = new Point(18, 180);
            keepNote.Size = new Size(384, 54);
            keepNote.Font = tipFont;
            keepNote.ForeColor = Color.FromArgb(120, 120, 130);
            keepNote.Text = "锁屏用全屏纯黑遮罩代替关闭显示器：唤醒时画面瞬间出现、不闪屏，\r\n" +
                            "任何截屏里也看不到遮罩。显示器背光保持点亮，视觉上仍是黑屏。";
            Controls.Add(keepNote);

            Label pathLabel = new Label();
            pathLabel.Text = "配置文件位置";
            pathLabel.Location = new Point(18, 244);
            pathLabel.Size = new Size(200, 20);
            pathLabel.Font = uiFont;
            pathLabel.ForeColor = Color.FromArgb(60, 60, 70);
            Controls.Add(pathLabel);

            TextBox pathBox = new TextBox();
            pathBox.Location = new Point(18, 266);
            pathBox.Size = new Size(384, 24);
            pathBox.Font = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            pathBox.ReadOnly = true;
            pathBox.Text = _config != null ? _config.FilePath : string.Empty;
            pathBox.SelectAll();
            Controls.Add(pathBox);

            Button okButton = new Button();
            okButton.Text = "确定";
            okButton.Location = new Point(216, 302);
            okButton.Size = new Size(84, 30);
            okButton.Font = uiFont;
            okButton.Click += OnOk;
            Controls.Add(okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.Location = new Point(312, 302);
            cancelButton.Size = new Size(84, 30);
            cancelButton.Font = uiFont;
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);

            Button openFolder = new Button();
            openFolder.Text = "打开所在文件夹";
            openFolder.Location = new Point(18, 302);
            openFolder.Size = new Size(130, 30);
            openFolder.Font = uiFont;
            openFolder.Click += OnOpenFolder;
            Controls.Add(openFolder);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private void OnOpenFolder(object sender, EventArgs e)
        {
            try
            {
                string path = _config != null ? _config.FilePath : string.Empty;
                string folder = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder) && System.IO.Directory.Exists(folder))
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + folder + "\"");
            }
            catch (Exception ex)
            {
                Log.Error("OptionsDialog.OnOpenFolder", ex);
            }
        }

        private void OnOk(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
