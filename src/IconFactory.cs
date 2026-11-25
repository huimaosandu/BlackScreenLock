using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BlackScreenLock
{
    /// <summary>
    /// 用代码直接绘制托盘/窗口图标，不依赖外部 .ico 文件，保证单 exe 免安装。
    /// 图形语义：深色屏幕 + 白色锁 = 已锁定的显示器。
    /// </summary>
    internal static class IconFactory
    {
        private static Icon _appIcon;
        private static IntPtr _iconHandle = IntPtr.Zero;
        private static Bitmap _iconBitmap;

        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null) BuildIcon();
                return _appIcon;
            }
        }

        /// <summary>把图标以 PNG 形式导出，仅用于自检/预览。</summary>
        public static void ExportPng(string path, int size)
        {
            Bitmap bmp = DrawBitmap(size);
            bmp.Save(path, ImageFormat.Png);
            bmp.Dispose();
        }

        private static void BuildIcon()
        {
            _iconBitmap = DrawBitmap(32);
            _iconHandle = _iconBitmap.GetHicon();
            _appIcon = Icon.FromHandle(_iconHandle);
        }

        private static Bitmap DrawBitmap(int size)
        {
            Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;

                // 背板：深色圆角方块
                RectangleF bg = new RectangleF(0.5f * s, 0.5f * s, 31f * s, 31f * s);
                using (GraphicsPath path = RoundedRect(bg, 5f * s))
                {
                    using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 24, 24, 28)))
                        g.FillPath(brush, path);
                    using (Pen pen = new Pen(Color.FromArgb(255, 72, 72, 82), 1f * s))
                        g.DrawPath(pen, path);
                }

                // 显示器外框
                RectangleF mon = new RectangleF(5f * s, 6.5f * s, 22f * s, 14f * s);
                using (Pen pen = new Pen(Color.FromArgb(255, 235, 235, 240), 1.6f * s))
                    g.DrawRectangle(pen, mon.X, mon.Y, mon.Width, mon.Height);

                // 支架
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 235, 235, 240)))
                {
                    g.FillRectangle(brush, 14.2f * s, 20.5f * s, 3.6f * s, 2.6f * s);
                    g.FillRectangle(brush, 9.5f * s, 23.1f * s, 13f * s, 1.8f * s);
                }

                // 屏幕里的锁：锁体 + 锁梁
                using (SolidBrush brush = new SolidBrush(Color.FromArgb(255, 250, 200, 60)))
                {
                    g.FillRectangle(brush, 12.4f * s, 12.6f * s, 7.2f * s, 5.6f * s);
                }
                using (Pen pen = new Pen(Color.FromArgb(255, 250, 200, 60), 1.3f * s))
                {
                    RectangleF arc = new RectangleF(13.4f * s, 9.6f * s, 5.2f * s, 5.2f * s);
                    g.DrawArc(pen, arc, 180f, 180f);
                    g.DrawLine(pen, 13.4f * s, 12.2f * s, 13.4f * s, 12.9f * s);
                    g.DrawLine(pen, 18.6f * s, 12.2f * s, 18.6f * s, 12.9f * s);
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float d = radius * 2f;
            path.AddArc(rect.X, rect.Y, d, d, 180f, 90f);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270f, 90f);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0f, 90f);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void Cleanup()
        {
            try
            {
                if (_appIcon != null)
                {
                    _appIcon.Dispose();
                    _appIcon = null;
                }
                if (_iconHandle != IntPtr.Zero)
                {
                    NativeMethods.DestroyIcon(_iconHandle);
                    _iconHandle = IntPtr.Zero;
                }
                if (_iconBitmap != null)
                {
                    _iconBitmap.Dispose();
                    _iconBitmap = null;
                }
            }
            catch { }
        }
    }
}
