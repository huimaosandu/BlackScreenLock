using System;
using System.Drawing;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 密码设置/修改对话框。
    /// 首次设置：只需输入新密码（不显示原密码框）。
    /// 之后修改：必须先通过原密码校验，才能改新密码。
    /// </summary>
    internal sealed class PasswordDialog : Form
    {
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
            ClientSize = new Size(400, _requireOld ? 288 : 226);
            Icon = IconFactory.AppIcon;
            Font = uiFont;
            BackColor = Color.FromArgb(250, 250, 252);

            int y = 16;

            if (_requireOld)
            {
                Controls.Add(MakeLabel("原密码", 18, y));
                y += 22;
                _oldBox = MakeTextBox(18, y, true);
                Controls.Add(_oldBox);
                y += 40;
            }

            Controls.Add(MakeLabel("新密码（4 ~ 64 位，字母、数字、符号均可）", 18, y));
            y += 22;
            _newBox = MakeTextBox(18, y, true);
            Controls.Add(_newBox);
            y += 40;

            Controls.Add(MakeLabel("确认新密码", 18, y));
            y += 22;
            _confirmBox = MakeTextBox(18, y, true);
            Controls.Add(_confirmBox);
            y += 40;

            _showBox = new CheckBox();
            _showBox.Text = "显示密码";
            _showBox.Location = new Point(18, y);
            _showBox.Size = new Size(120, 22);
            _showBox.Font = uiFont;
            _showBox.CheckedChanged += OnShowChanged;
            Controls.Add(_showBox);
            y += 34;

            _hint = new Label();
            _hint.Location = new Point(18, y);
            _hint.Size = new Size(364, 40);
            _hint.Font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
            _hint.ForeColor = Color.FromArgb(110, 110, 120);
            _hint.Text = "提示：黑屏锁定后屏幕不可见，解锁需要在键盘上盲打密码再按回车；\n解锁快捷键默认 Ctrl + Alt + L，可在托盘菜单中修改。";
            Controls.Add(_hint);

            _okButton = new Button();
            _okButton.Text = "确定";
            _okButton.Location = new Point(196, ClientSize.Height - 44);
            _okButton.Size = new Size(84, 30);
            _okButton.Font = uiFont;
            _okButton.Click += OnOk;
            Controls.Add(_okButton);

            _cancelButton = new Button();
            _cancelButton.Text = "取消";
            _cancelButton.Location = new Point(292, ClientSize.Height - 44);
            _cancelButton.Size = new Size(84, 30);
            _cancelButton.Font = uiFont;
            _cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(_cancelButton);

            AcceptButton = _okButton;
            CancelButton = _cancelButton;

            if (_requireOld) _oldBox.Focus();
            else _newBox.Focus();
        }

        private static Label MakeLabel(string text, int x, int y)
        {
            Label label = new Label();
            label.Text = text;
            label.Location = new Point(x, y);
            label.Size = new Size(364, 20);
            label.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
            label.ForeColor = Color.FromArgb(60, 60, 70);
            return label;
        }

        private static TextBox MakeTextBox(int x, int y, bool isPassword)
        {
            TextBox box = new TextBox();
            box.Location = new Point(x, y);
            box.Size = new Size(364, 26);
            box.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
            if (isPassword) box.PasswordChar = '●';
            box.MaxLength = 64;
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
