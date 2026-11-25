using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 快捷键编辑对话框：点击捕获框后直接按下目标组合键即可。
    /// 规则：必须包含 Ctrl 或 Alt；F1~F24 可单独使用；回车/退格/Esc/Tab/空格 不作为热键。
    /// </summary>
    internal sealed class HotkeyDialog : Form
    {
        private readonly int _originalModifiers;
        private readonly int _originalVk;

        private int _modifiers;
        private int _vk;

        private TextBox _capture;
        private Label _warn;
        private Button _okButton;

        public int ResultModifiers { get { return _modifiers; } }
        public int ResultVk { get { return _vk; } }
        /// <summary>用户是否真的改动了热键（没改动则调用方无需重新注册）。</summary>
        public bool Changed
        {
            get { return _modifiers != _originalModifiers || _vk != _originalVk; }
        }

        public HotkeyDialog(int modifiers, int vk)
        {
            _originalModifiers = modifiers;
            _originalVk = vk;
            _modifiers = modifiers;
            _vk = vk;
            BuildUi();
        }

        private void BuildUi()
        {
            Font uiFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            Text = "编辑快捷键";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 232);
            Icon = IconFactory.AppIcon;
            Font = uiFont;
            BackColor = Color.FromArgb(250, 250, 252);

            Label tip = new Label();
            tip.Text = "点击下面的输入框，然后直接按下想要的组合键：";
            tip.Location = new Point(18, 16);
            tip.Size = new Size(364, 20);
            tip.Font = uiFont;
            tip.ForeColor = Color.FromArgb(60, 60, 70);
            Controls.Add(tip);

            _capture = new TextBox();
            _capture.Location = new Point(18, 42);
            _capture.Size = new Size(364, 34);
            _capture.Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold, GraphicsUnit.Point);
            _capture.ReadOnly = true;
            _capture.TextAlign = HorizontalAlignment.Center;
            _capture.BackColor = Color.White;
            _capture.ShortcutsEnabled = false;
            _capture.Cursor = Cursors.Hand;
            _capture.Text = KeyMapper.HotkeyText(_modifiers, _vk);
            _capture.Enter += OnCaptureEnter;
            _capture.Click += OnCaptureEnter;
            Controls.Add(_capture);

            _warn = new Label();
            _warn.Location = new Point(18, 86);
            _warn.Size = new Size(364, 40);
            _warn.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _warn.ForeColor = Color.FromArgb(110, 110, 120);
            _warn.Text = "要求：至少包含 Ctrl 或 Alt；F1 ~ F24 可以单独作为快捷键。\r\n不支持的组合会在下方给出提示。";
            Controls.Add(_warn);

            Button resetButton = new Button();
            resetButton.Text = "恢复默认";
            resetButton.Location = new Point(18, 140);
            resetButton.Size = new Size(96, 30);
            resetButton.Font = uiFont;
            resetButton.Click += OnReset;
            Controls.Add(resetButton);

            _okButton = new Button();
            _okButton.Text = "确定";
            _okButton.Location = new Point(196, 140);
            _okButton.Size = new Size(84, 30);
            _okButton.Font = uiFont;
            _okButton.Click += OnOk;
            Controls.Add(_okButton);

            Button cancelButton = new Button();
            cancelButton.Text = "取消";
            cancelButton.Location = new Point(292, 140);
            cancelButton.Size = new Size(84, 30);
            cancelButton.Font = uiFont;
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);

            Label footer = new Label();
            footer.Location = new Point(18, 182);
            footer.Size = new Size(364, 36);
            footer.Font = new Font("Microsoft YaHei UI", 8f, FontStyle.Regular, GraphicsUnit.Point);
            footer.ForeColor = Color.FromArgb(140, 140, 150);
            footer.Text = "快捷键仅在进入桌面后生效；开机密码仍由系统账户密码控制。";
            Controls.Add(footer);

            AcceptButton = _okButton;
            CancelButton = cancelButton;
        }

        private void OnCaptureEnter(object sender, EventArgs e)
        {
            _capture.SelectAll();
        }

        private void OnReset(object sender, EventArgs e)
        {
            _modifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;
            _vk = NativeMethods.VK_L;
            RefreshCapture();
        }

        private void RefreshCapture()
        {
            _capture.Text = KeyMapper.HotkeyText(_modifiers, _vk);
            _warn.ForeColor = Color.FromArgb(110, 110, 120);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_capture == null || !_capture.Focused)
                return base.ProcessCmdKey(ref msg, keyData);

            Keys code = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;

            // 单独按下修饰键：忽略，继续等待
            if (code == Keys.ControlKey || code == Keys.ShiftKey || code == Keys.Menu ||
                code == Keys.LWin || code == Keys.RWin || code == Keys.None)
                return true;

            // 无修饰的回车/Esc 交给对话框本身（确认 / 取消）
            int m = 0;
            if ((mods & Keys.Control) == Keys.Control) m |= NativeMethods.MOD_CONTROL;
            if ((mods & Keys.Alt) == Keys.Alt) m |= NativeMethods.MOD_ALT;
            if ((mods & Keys.Shift) == Keys.Shift) m |= NativeMethods.MOD_SHIFT;

            if (m == 0 && (code == Keys.Enter || code == Keys.Escape))
                return base.ProcessCmdKey(ref msg, keyData);

            int vk = (int)code;
            if (!KeyMapper.IsAcceptableHotkey(m, vk))
            {
                _warn.ForeColor = Color.FromArgb(200, 60, 60);
                _warn.Text = "该组合不能用作全局快捷键：\r\n需要至少包含 Ctrl 或 Alt（F1~F24 可单独使用），且不能是回车/退格/Esc/Tab/空格。";
                return true;
            }

            _modifiers = m;
            _vk = vk;
            RefreshCapture();
            return true;
        }

        private void OnOk(object sender, EventArgs e)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
