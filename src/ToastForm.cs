using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 轻量提示气泡：无边框、置顶、鼠标穿透、不抢焦点。
    /// 只在“关屏前”与“解锁后”短暂显示，锁屏期间不存在任何可见窗口，故不干扰 UIA 与截图。
    /// </summary>
    internal sealed class ToastForm : Form
    {
        private static ToastForm _current;

        private string _title = string.Empty;
        private string _detail = string.Empty;
        private Font _titleFont;
        private Font _detailFont;
        private Timer _closeTimer;
        private int _titleHeight = 20;
        private int _detailHeight = 16;

        private ToastForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(32, 32, 36);
            DoubleBuffered = true;
            _titleFont = new Font("Microsoft YaHei UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
            _detailFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT（鼠标穿透）
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        public static void ShowToast(string title, string detail, int durationMs)
        {
            try
            {
                HideCurrent();
                ToastForm form = new ToastForm();
                form._title = title ?? string.Empty;
                form._detail = detail ?? string.Empty;
                form.BuildLayout();
                form.PlaceBottomCenter();
                form.Show();
                _current = form;
                form.StartAutoClose(durationMs);
            }
            catch (Exception ex)
            {
                Log.Error("ToastForm.ShowToast", ex);
            }
        }

        public static void HideCurrent()
        {
            if (_current == null) return;
            ToastForm form = _current;
            _current = null;
            try { form.CloseNow(); } catch { }
        }

        private void BuildLayout()
        {
            const int maxWidth = 460;
            Size titleSize = TextRenderer.MeasureText(_title, _titleFont, new Size(maxWidth, int.MaxValue), TextFormatFlags.WordBreak);
            Size detailSize = _detail.Length == 0
                ? new Size(0, 0)
                : TextRenderer.MeasureText(_detail, _detailFont, new Size(maxWidth, int.MaxValue), TextFormatFlags.WordBreak);

            _titleHeight = titleSize.Height;
            _detailHeight = detailSize.Height;

            int contentWidth = Math.Max(titleSize.Width, detailSize.Width);
            int width = Math.Min(maxWidth, contentWidth) + 48;
            int height = _titleHeight + (_detailHeight > 0 ? _detailHeight + 8 : 0) + 34;
            ClientSize = new Size(Math.Max(width, 220), Math.Max(height, 64));

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), 10))
                Region = new Region(path);
        }

        private void PlaceBottomCenter()
        {
            Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
            int x = work.Left + (work.Width - Width) / 2;
            int y = work.Top + work.Height - Height - 100;
            Location = new Point(x, y);
        }

        private void StartAutoClose(int durationMs)
        {
            _closeTimer = new Timer();
            _closeTimer.Interval = durationMs < 500 ? 500 : durationMs;
            _closeTimer.Tick += OnCloseTick;
            _closeTimer.Start();
        }

        private void OnCloseTick(object sender, EventArgs e)
        {
            if (_closeTimer != null)
            {
                _closeTimer.Stop();
                _closeTimer.Dispose();
                _closeTimer = null;
            }
            if (_current == this) _current = null;
            CloseNow();
        }

        private void CloseNow()
        {
            try
            {
                if (_closeTimer != null)
                {
                    _closeTimer.Stop();
                    _closeTimer.Dispose();
                    _closeTimer = null;
                }
                Close();
                Dispose();
            }
            catch { }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            using (GraphicsPath path = RoundedRect(new Rectangle(1, 1, ClientSize.Width - 2, ClientSize.Height - 2), 10))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 28, 28, 32)))
                    e.Graphics.FillPath(brush, path);
                using (Pen pen = new Pen(Color.FromArgb(255, 62, 62, 70), 1f))
                    e.Graphics.DrawPath(pen, path);
            }

            Rectangle titleRect = new Rectangle(24, 16, ClientSize.Width - 48, _titleHeight);
            TextRenderer.DrawText(e.Graphics, _title, _titleFont, titleRect, Color.FromArgb(255, 245, 245, 250),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            if (_detail.Length > 0)
            {
                Rectangle detailRect = new Rectangle(24, 16 + _titleHeight + 8, ClientSize.Width - 48, _detailHeight);
                TextRenderer.DrawText(e.Graphics, _detail, _detailFont, detailRect, Color.FromArgb(255, 168, 168, 180),
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        private static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_current == this) _current = null;
        }
    }
}
