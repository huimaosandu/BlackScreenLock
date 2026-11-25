using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 密码设置/修改对话框。
    /// 首次设置：只需输入新密码（不显示原密码框）。
    /// 之后修改：必须先通过原密码校验，才能改新密码。
    /// 底部固定一行“确认 / 取消”，与提示文字分区，不会被遮挡。
    /// </summary>
    internal sealed class PasswordDialog : Form
    {
        private const int SideMargin = 20;
        private const int ContentWidth = 380;

        private readonly Config _config;
        private readonly bool _requireOld;

        private TextBox _oldBox;
        private TextBox _newBox;
        private TextBox _confirmBox;
        private CheckBox _showBox;
        private Label _hint;
        private Button _okButton;
        private Button _cancelButton;

        public PasswordDialog(Config config)
        {
            _config = config;
            _requireOld = config != null && config.HasPassword;
            BuildUi();
        }

        private void BuildUi()
        {
            Font uiFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

            Text = _requireOld ? "修改解锁密码" : "设置解锁密码";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Icon = IconFactory.AppIcon;
            Font = uiFont;
            BackColor = Color.FromArgb(250, 250, 252);

            int y = 16;

            if (_requireOld)
            {
                AddLabel("原密码（必须正确才能修改）", y, uiFont);
                y += 22;
                _oldBox = AddTextBox(y);
                y += 40;
            }

            AddLabel("新密码（4 ~ 64 位，字母、数字、符号均可）", y, uiFont);
            y += 22;
            _newBox = AddTextBox(y);
            y += 40;

            AddLabel("确认新密码", y, uiFont);
            y += 22;
            _confirmBox = AddTextBox(y);
            y += 40;

            _showBox = new CheckBox();
            _showBox.Text = "显示密码";
            _showBox.Location = new Point(SideMargin, y);
            _showBox.Size = new Size(160, 22);
            _showBox.Font = uiFont;
            _showBox.CheckedChanged += OnShowChanged;
            Controls.Add(_showBox);
            y += 30;

            _hint = new Label();
            _hint.Location = new Point(SideMargin, y);
            _hint.Size = new Size(ContentWidth, 58);
            _hint.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _hint.ForeColor = Color.FromArgb(110, 110, 120);
            _hint.Text = "锁定后屏幕不可见，解锁时需要盲打此密码再按回车。\r\n解锁不区分字母大小写（CapsLock 开或关都能解开）。\r\n解锁快捷键可在托盘右键菜单「编辑快捷键」中修改。";
            Controls.Add(_hint);
            y += 66;

            // ---- 按钮行：独立分区，不再与提示文字重叠 ----
            int buttonY = y;
            int cancelX = SideMargin + ContentWidth - 96;
            int okX = cancelX - 8 - 96;

            _okButton = new Button();
            _okButton.Text = "确认";
            _okButton.Location = new Point(okX, buttonY);
            _okButton.Size = new Size(96, 32);
            _okButton.Font = uiFont;
            _okButton.Click += OnOk;
            Controls.Add(_okButton);

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.Location = new Point(cancelX, buttonY);
            _cancelButton.Size = new Size(96, 32);
            _cancelButton.Font = uiFont;
            _cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancelButton);

            ClientSize = new Size(SideMargin * 2 + ContentWidth, buttonY + 32 + 16);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            if (_requireOld) _oldBox.Focus();
            else _newBox.Focus();
        }

        private void AddLabel(string text, int y, Font font)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(SideMargin, y);
            label.Size = new Size(ContentWidth, 20);
            label.Font = font;
            label.ForeColor = Color.FromArgb(60, 60, 70);
            Controls.Add(label);
        }

        private TextBox AddTextBox(int y)
        {
            TextBox box = new TextBox();
            box.Location = new Point(SideMargin, y);
            box.Size = new Size(ContentWidth, 26);
            box.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            box.PasswordChar = '●';
            box.MaxLength = 64;
            Controls.Add(box);
            return box;
        }

        private void OnShowChanged(object sender, EventArgs e)
        {
            char pc = _showBox.Checked ? '\0' : '●';
            if (_oldBox != null) _oldBox.PasswordChar = pc;
            _newBox.PasswordChar = pc;
            _confirmBox.PasswordChar = pc;
        }

        private void OnOk(object sender, EventArgs e)
        {
            if (_requireOld)
            {
                if (!_config.VerifyPassword(_oldBox.Text))
                {
                    MessageBox.Show(this, "原密码不正确，无法修改。", "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _oldBox.SelectAll();
                    _oldBox.Focus();
                    return;
                }
            }

            string pwd = _newBox.Text;
            if (pwd.Length < 4)
            {
                MessageBox.Show(this, "新密码至少 4 位。", "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _newBox.Focus();
                return;
            }

            // 锁屏后是盲打，只能用键面能直接敲出来的 ASCII 字符，中文/全角字符打不出来
            foreach (char c in pwd)
            {
                if (c < 0x20 || c > 0x7E)
                {
                    MessageBox.Show(this, "密码只能使用英文字母、数字和英文符号。\n不能包含中文、全角字符或不可见字符，否则黑屏时无法盲打输入。",
                        "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _newBox.Focus();
                    return;
                }
            }

            if (pwd != _confirmBox.Text)
            {
                MessageBox.Show(this, "两次输入的新密码不一致。", "黑屏锁", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                _confirmBox.SelectAll();
                _confirmBox.Focus();
                return;
            }

            _config.SetPassword(pwd);
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
