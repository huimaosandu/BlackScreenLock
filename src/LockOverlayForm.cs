using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BlackScreenLock
{
    /// <summary>
    /// 锁屏遮罩：覆盖整个虚拟屏幕的单窗口。
    ///
    /// 两种形态：
    ///   Idle   —— 纯黑不透明、鼠标穿透、物理键鼠被钩子吞掉（屏幕同时被 DPMS 关闭，肉眼全黑）
    ///   Prompt —— 半透明深色 + 居中磨砂卡片 + 密码圆点 + 解锁/重新黑屏按钮
    ///
    /// 三个关键约束：
    ///   1) SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)：遮罩肉眼可见，但任何截屏都拿不到它，
    ///      截图依旧得到真实桌面画面；
    ///   2) WS_EX_NOACTIVATE：永不抢焦点，自动化程序的前台窗口不会被打断；
    ///   3) Idle 形态挂 WS_EX_TRANSPARENT 鼠标穿透，注入的坐标点击可以穿过遮罩命中真实目标窗口。
    /// </summary>
    internal sealed class LockOverlayForm : Form
    {
        private const float PromptOpacity = 1.0f;   // 磨砂背景本身已不透明，窗口不再需要整体降透明

        private enum OverlayMode
        {
            Idle,
            Prompt
        }

        private const int CardWidth = 468;
        private const int CardHeight = 256;

        private OverlayMode _mode = OverlayMode.Idle;
        private int _dotCount;
        private string _errorText = string.Empty;
        private int _errorUntilTick;
        private bool _captureExcluded;
        private Timer _refreshTimer;
        private Bitmap _frosted;

        private Rectangle _submitRect;
        private Rectangle _dismissRect;
        private bool _submitHover;
        private bool _dismissHover;

        private readonly Font _titleFont = new Font("Microsoft YaHei UI", 14f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _subFont = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _hintFont = new Font("Microsoft YaHei UI", 10f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _buttonFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _errorFont = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular, GraphicsUnit.Point);

        public event Action SubmitRequested;
        public event Action DismissRequested;

        public bool CaptureExcluded { get { return _captureExcluded; } }

        public LockOverlayForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Bounds = SystemInformation.VirtualScreen;
            TopMost = true;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            DoubleBuffered = true;
            Cursor = Cursors.Default;
            // 固定标题：自动化脚本遍历顶层窗口时可按这个名字显式过滤掉遮罩
            Text = "BlackScreenLock.Overlay";

            // 提前创建句柄并设置“截图排除”，避免第一次显示时被截到一帧黑屏
            IntPtr handle = Handle;
            ApplyCaptureExclusion();
            SetMouseTransparent(true);
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
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW;
                cp.ExStyle |= NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        // ---------------- 对外状态控制 ----------------

        public void ShowOverlay()
        {
            if (!Visible) Show();
            TopMost = true;
            ApplyCaptureExclusion();
        }

        public void HideOverlay()
        {
            if (Visible) Hide();
        }

        public void EnterIdle()
        {
            _mode = OverlayMode.Idle;
            _dotCount = 0;
            _errorText = string.Empty;
            _submitHover = false;
            _dismissHover = false;
            ReleaseFrosted();
            StopRefreshTimer();
            Opacity = 1.0;
            SetMouseTransparent(true);
            ApplyCaptureExclusion();
            Invalidate();
        }

        public void EnterPrompt()
        {
            _mode = OverlayMode.Prompt;
            _errorText = string.Empty;
            Opacity = PromptOpacity;
            ApplyCaptureExclusion();

            // 先把深色底 + 卡片画出来，按下按键的瞬间就能看到反应
            Invalidate();
            Update();

            // 再取一张真实桌面做磨砂背景，画第二帧
            BuildFrostedBackdrop();
            Invalidate();

            StartRefreshTimer();
        }

        /// <summary>
        /// 抓一张真实桌面（遮罩被 WDA 排除，抓到的画面里没有遮罩），模糊成磨砂玻璃。
        /// 失败时留空，绘制阶段回退到深色底色。
        /// </summary>
        private void BuildFrostedBackdrop()
        {
            ReleaseFrosted();
            _frosted = ScreenBlur.Capture(Bounds, 8, 3, Color.FromArgb(10, 10, 16), 118);
            if (_frosted == null) Log.Write("磨砂背景生成失败，回退为深色底色");
        }

        private void ReleaseFrosted()
        {
            if (_frosted == null) return;
            try { _frosted.Dispose(); } catch { }
            _frosted = null;
        }

        public void SetDotCount(int count)
        {
            if (count < 0) count = 0;
            if (count > 64) count = 64;
            if (_dotCount == count) return;
            _dotCount = count;
            if (_errorText.Length > 0)
            {
                _errorText = string.Empty;
                _errorUntilTick = 0;
            }
            Invalidate();
        }

        public void ShowError(string message, int durationMs)
        {
            _errorText = message ?? string.Empty;
            _errorUntilTick = Environment.TickCount + durationMs;
            _dotCount = 0;
            Invalidate();
            StartRefreshTimer();
        }

        private void StartRefreshTimer()
        {
            if (_refreshTimer != null) return;
            _refreshTimer = new Timer();
            _refreshTimer.Interval = 1000;
            _refreshTimer.Tick += OnRefreshTick;
            _refreshTimer.Start();
        }

        private void StopRefreshTimer()
        {
            if (_refreshTimer == null) return;
            try
            {
                _refreshTimer.Stop();
                _refreshTimer.Dispose();
            }
            catch { }
            _refreshTimer = null;
        }

        private void OnRefreshTick(object sender, EventArgs e)
        {
            if (_mode != OverlayMode.Prompt)
            {
                StopRefreshTimer();
                return;
            }

            // 保险：普通补设（幂等，不产生任何可见帧）
            ApplyCaptureExclusion();

            if (_errorText.Length > 0 && Environment.TickCount >= _errorUntilTick)
            {
                _errorText = string.Empty;
                Invalidate();
            }
        }

        public void ApplyCaptureExclusion()
        {
            ApplyCaptureExclusion(false);
        }

        /// <summary>
        /// 设置“从截屏排除”。<paramref name="forceToggle"/> = true 时先设 NONE 再设回，
        /// 用于显示器开关导致 DWM 重建合成表面、旧属性被丢掉的情况（单纯重复设置无效）。
        /// </summary>
        public void ApplyCaptureExclusion(bool forceToggle)
        {
            try
            {
                if (forceToggle)
                    NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_NONE);

                int rc = NativeMethods.SetWindowDisplayAffinity(Handle, NativeMethods.WDA_EXCLUDEFROMCAPTURE);
                _captureExcluded = rc != 0;
                if (!_captureExcluded)
                    Log.Write("SetWindowDisplayAffinity 失败，遮罩可能会被截图拍到");
            }
            catch (Exception ex)
            {
                Log.Error("LockOverlayForm.ApplyCaptureExclusion", ex);
            }
        }

        public void SetMouseTransparent(bool transparent)
        {
            try
            {
                int style = NativeMethods.GetWindowLong(Handle, NativeMethods.GWL_EXSTYLE);
                if (transparent) style |= NativeMethods.WS_EX_TRANSPARENT;
                else style &= ~NativeMethods.WS_EX_TRANSPARENT;
                NativeMethods.SetWindowLong(Handle, NativeMethods.GWL_EXSTYLE, style);
                NativeMethods.SetWindowPos(
                    Handle, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                    NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE |
                    NativeMethods.SWP_FRAMECHANGED);
            }
            catch (Exception ex)
            {
                Log.Error("LockOverlayForm.SetMouseTransparent", ex);
            }
        }

        // ---------------- 鼠标交互 ----------------

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_mode == OverlayMode.Prompt)
            {
                bool submitHover = _submitRect.Contains(e.Location);
                bool dismissHover = _dismissRect.Contains(e.Location);
                if (submitHover != _submitHover || dismissHover != _dismissHover)
                {
                    _submitHover = submitHover;
                    _dismissHover = dismissHover;
                    Cursor = (submitHover || dismissHover) ? Cursors.Hand : Cursors.Default;
                    Invalidate();
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (_mode == OverlayMode.Prompt && e.Button == MouseButtons.Left)
            {
                if (_submitRect.Contains(e.Location))
                {
                    Action submit = SubmitRequested;
                    if (submit != null) submit();
                    return;
                }
                if (_dismissRect.Contains(e.Location))
                {
                    Action dismiss = DismissRequested;
                    if (dismiss != null) dismiss();
                    return;
                }
            }
            base.OnMouseDown(e);
        }

        // ---------------- 绘制 ----------------

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 背景全部由 OnPaint 绘制（磨砂图或纯黑），这里不填充，避免闪烁
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            PaintContent(g, ClientSize);
        }

        /// <summary>把当前界面画到任意画布（自检导出预览也走这里）。</summary>
        internal void PaintContent(Graphics g, Size canvas)
        {
            if (_mode == OverlayMode.Idle)
            {
                g.Clear(Color.Black);
                return;
            }

            if (_frosted != null && canvas.Width == Bounds.Width && canvas.Height == Bounds.Height)
            {
                // 真实窗口：铺满磨砂玻璃背景（抓的是真实桌面，模糊后叠深色）
                g.DrawImage(_frosted, 0, 0, canvas.Width, canvas.Height);
            }
            else
            {
                // 回退底色（抓屏失败时）或导出预览
                using (LinearGradientBrush brush = new LinearGradientBrush(
                    new Rectangle(0, 0, Math.Max(1, canvas.Width), Math.Max(1, canvas.Height)),
                    Color.FromArgb(255, 22, 22, 29), Color.FromArgb(255, 8, 8, 12), 90f))
                {
                    g.FillRectangle(brush, 0, 0, canvas.Width, canvas.Height);
                }
            }

            int cw = CardWidth;
            int ch = CardHeight;
            int centerX;
            int centerY;

            if (canvas.Width == ClientSize.Width && canvas.Height == ClientSize.Height)
            {
                // 真实全屏窗口：卡片对准主显示器中心（多屏时不会掉进屏幕缝隙）
                Rectangle primary = Screen.PrimaryScreen.Bounds;
                centerX = (primary.Left - Bounds.Left) + primary.Width / 2;
                centerY = (primary.Top - Bounds.Top) + primary.Height / 2;
            }
            else
            {
                // 导出预览：按画布中心
                centerX = canvas.Width / 2;
                centerY = canvas.Height / 2;
            }

            Rectangle card = new Rectangle(centerX - cw / 2, centerY - ch / 2, cw, ch);

            // 卡片外阴影
            for (int i = 3; i >= 1; i--)
            {
                using (GraphicsPath shadowPath = RoundedRect(
                    new Rectangle(card.X - i * 5, card.Y - i * 5, card.Width + i * 10, card.Height + i * 10), 18 + i * 3))
                using (SolidBrush shadowBrush = new SolidBrush(Color.FromArgb(28, 0, 0, 0)))
                {
                    g.FillPath(shadowBrush, shadowPath);
                }
            }

            // 卡片主体（略带透明度，让磨砂背景透上来，像一块玻璃卡片）
            using (GraphicsPath cardPath = RoundedRect(card, 18))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(238, 27, 27, 34)))
                    g.FillPath(brush, cardPath);
                using (LinearGradientBrush glaze = new LinearGradientBrush(
                    new Rectangle(card.X, card.Y, card.Width, 90),
                    Color.FromArgb(20, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f))
                {
                    g.SetClip(cardPath);
                    g.FillRectangle(glaze, card.X, card.Y, card.Width, 90);
                    g.ResetClip();
                }
                using (Pen pen = new Pen(Color.FromArgb(255, 54, 54, 66), 1f))
                    g.DrawPath(pen, cardPath);
            }

            int left = card.X + 28;
            int right = card.Right - 28;

            // 锁图标
            DrawLockIcon(g, card.X + 26, card.Y + 24);

            TextRenderer.DrawText(g, "屏幕已锁定", _titleFont,
                new Rectangle(card.X + 60, card.Y + 20, card.Width - 90, 26),
                Color.FromArgb(255, 244, 244, 250), TextFormatFlags.NoPrefix);

            TextRenderer.DrawText(g, "输入密码后按回车解锁，Esc 清空", _subFont,
                new Rectangle(card.X + 60, card.Y + 48, card.Width - 90, 20),
                Color.FromArgb(255, 142, 142, 160), TextFormatFlags.NoPrefix);

            using (Pen pen = new Pen(Color.FromArgb(255, 46, 46, 58), 1f))
                g.DrawLine(pen, left, card.Y + 84, right, card.Y + 84);

            // 密码输入框
            Rectangle input = new Rectangle(left, card.Y + 102, right - left, 52);
            using (GraphicsPath inputPath = RoundedRect(input, 10))
            {
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 13, 13, 18)))
                    g.FillPath(brush, inputPath);
                using (Pen pen = new Pen(Color.FromArgb(255, 60, 60, 74), 1f))
                    g.DrawPath(pen, inputPath);
            }

            if (_dotCount == 0)
            {
                TextRenderer.DrawText(g, "请输入密码", _hintFont,
                    new Rectangle(input.X + 18, input.Y, input.Width - 36, input.Height),
                    Color.FromArgb(255, 88, 88, 104),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            else
            {
                int dotY = input.Y + input.Height / 2;
                int startX = input.X + 22;
                int maxDots = Math.Max(1, (input.Width - 44) / 14);
                int shown = Math.Min(_dotCount, maxDots);
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 234, 234, 242)))
                {
                    for (int i = 0; i < shown; i++)
                        g.FillEllipse(brush, startX + i * 14, dotY - 3.5f, 7f, 7f);
                }
            }

            // 状态行：错误提示 / 已输入提示
            Rectangle statusRect = new Rectangle(left, card.Y + 162, right - left, 22);
            if (_errorText.Length > 0 && Environment.TickCount < _errorUntilTick)
            {
                TextRenderer.DrawText(g, _errorText, _errorFont, statusRect,
                    Color.FromArgb(255, 255, 92, 96), TextFormatFlags.NoPrefix);
            }
            else if (_dotCount > 0)
            {
                TextRenderer.DrawText(g, "已输入 " + _dotCount + " 个字符，按回车提交", _errorFont, statusRect,
                    Color.FromArgb(255, 122, 122, 138), TextFormatFlags.NoPrefix);
            }

            // 按钮
            Rectangle dismiss = new Rectangle(right - 96, card.Y + 198, 96, 40);
            Rectangle submit = new Rectangle(dismiss.X - 12 - 112, card.Y + 198, 112, 40);
            _submitRect = submit;
            _dismissRect = dismiss;

            using (GraphicsPath submitPath = RoundedRect(submit, 8))
            {
                Color fill = _submitHover
                    ? Color.FromArgb(255, 66, 126, 255)
                    : Color.FromArgb(255, 47, 107, 255);
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, submitPath);
            }
            TextRenderer.DrawText(g, "解锁", _buttonFont, submit, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

            using (GraphicsPath dismissPath = RoundedRect(dismiss, 8))
            {
                Color fill = _dismissHover
                    ? Color.FromArgb(255, 44, 44, 56)
                    : Color.FromArgb(255, 30, 30, 38);
                using (SolidBrush brush = new SolidBrush(fill))
                    g.FillPath(brush, dismissPath);
                using (Pen pen = new Pen(Color.FromArgb(255, 68, 68, 82), 1f))
                    g.DrawPath(pen, dismissPath);
            }
            TextRenderer.DrawText(g, "重新黑屏", _subFont, dismiss, Color.FromArgb(255, 200, 200, 214),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private void DrawLockIcon(Graphics g, int x, int y)
        {
            using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 232, 185, 60)))
            {
                g.FillRectangle(brush, x + 2, y + 10, 20, 14);
            }
            using (Pen pen = new Pen(Color.FromArgb(255, 232, 185, 60), 2f))
            {
                g.DrawArc(pen, x + 6, y + 2, 12, 14, 180f, 180f);
                g.DrawLine(pen, x + 6, y + 9, x + 6, y + 12);
                g.DrawLine(pen, x + 18, y + 9, x + 18, y + 12);
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

        /// <summary>自检用：把 Prompt 形态渲染成 PNG，便于核对界面（不影响真实屏幕）。</summary>
        internal void ExportPreview(string path)
        {
            OverlayMode oldMode = _mode;
            int oldDots = _dotCount;

            _mode = OverlayMode.Prompt;
            _dotCount = 4;

            int canvasW = CardWidth + 80;
            int canvasH = CardHeight + 80;

            using (Bitmap bmp = new Bitmap(canvasW, canvasH))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.FromArgb(255, 44, 48, 58));
                    PaintContent(g, new Size(canvasW, canvasH));
                }
                bmp.Save(path, ImageFormat.Png);
            }

            _mode = oldMode;
            _dotCount = oldDots;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try
                {
                    if (_refreshTimer != null)
                    {
                        _refreshTimer.Stop();
                        _refreshTimer.Dispose();
                        _refreshTimer = null;
                    }
                }
                catch { }
                try
                {
                    _titleFont.Dispose();
                    _subFont.Dispose();
                    _hintFont.Dispose();
                    _buttonFont.Dispose();
                    _errorFont.Dispose();
                }
                catch { }
                ReleaseFrosted();
            }
            base.Dispose(disposing);
        }
    }
}
