using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BlackScreenLock
{
    /// <summary>
    /// 生成“磨砂玻璃”背景：抓一张当前真实桌面 -> 大幅缩小 -> 多次箱式模糊 -> 放大回来 -> 叠深色。
    ///
    /// 为什么不用 SetWindowCompositionAttribute 的 Acrylic：那是未公开 API，且与 layered 窗口、
    /// Windows 版本强相关，稳定性不可控。这里用纯 GDI 自己做，效果可控、失败可回退。
    ///
    /// 为什么不影响截图：遮罩窗口本身挂了 WDA_EXCLUDEFROMCAPTURE，抓屏接口（含 GDI BitBlt）
    /// 看到的是被排除遮罩之后的真实桌面，所以这里抓到的背景是干净的桌面，不会出现“截自己的黑屏”。
    /// </summary>
    internal static class ScreenBlur
    {
        public static Bitmap Capture(Rectangle screenBounds, int downscale, int radius, Color dimColor, int dimAlpha)
        {
            try
            {
                int w = screenBounds.Width;
                int h = screenBounds.Height;
                if (w <= 0 || h <= 0) return null;
                if (downscale < 1) downscale = 1;

                int sw = Math.Max(1, w / downscale);
                int sh = Math.Max(1, h / downscale);

                using (Bitmap full = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(screenBounds.Left, screenBounds.Top, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
                    }

                    using (Bitmap small = new Bitmap(sw, sh, PixelFormat.Format32bppArgb))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.DrawImage(full, new Rectangle(0, 0, sw, sh));
                        }

                        BoxBlur(small, radius);
                        BoxBlur(small, radius);

                        Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                        using (Graphics g = Graphics.FromImage(result))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.CompositingQuality = CompositingQuality.HighQuality;
                            g.DrawImage(small, new Rectangle(0, 0, w, h));
                            using (SolidBrush brush = new SolidBrush(Color.FromArgb(dimAlpha, dimColor)))
                                g.FillRectangle(brush, 0, 0, w, h);
                        }
                        return result;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("ScreenBlur.Capture", ex);
                return null;
            }
        }

        /// <summary>两趟箱式模糊（横向 + 纵向），在缩小后的位图上跑，开销极小。</summary>
        private static void BoxBlur(Bitmap bmp, int radius)
        {
            if (radius < 1) return;

            int w = bmp.Width;
            int h = bmp.Height;
            BitmapData data = bmp.LockBits(
                new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] buffer = new byte[stride * h];
                byte[] temp = new byte[stride * h];
                Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);

                BlurPass(buffer, temp, w, h, stride, radius, true);
                BlurPass(temp, buffer, w, h, stride, radius, false);

                Marshal.Copy(buffer, 0, data.Scan0, buffer.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static void BlurPass(byte[] src, byte[] dst, int w, int h, int stride, int radius, bool horizontal)
        {
            int window = radius * 2 + 1;

            if (horizontal)
            {
                for (int y = 0; y < h; y++)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x++)
                    {
                        int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int sx = x + k;
                            if (sx < 0) sx = 0;
                            else if (sx >= w) sx = w - 1;
                            int idx = row + sx * 4;
                            sumB += src[idx];
                            sumG += src[idx + 1];
                            sumR += src[idx + 2];
                            sumA += src[idx + 3];
                        }
                        int o = row + x * 4;
                        dst[o] = (byte)(sumB / window);
                        dst[o + 1] = (byte)(sumG / window);
                        dst[o + 2] = (byte)(sumR / window);
                        dst[o + 3] = (byte)(sumA / window);
                    }
                }
            }
            else
            {
                for (int x = 0; x < w; x++)
                {
                    int col = x * 4;
                    for (int y = 0; y < h; y++)
                    {
                        int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                        for (int k = -radius; k <= radius; k++)
                        {
                            int sy = y + k;
                            if (sy < 0) sy = 0;
                            else if (sy >= h) sy = h - 1;
                            int idx = sy * stride + col;
                            sumB += src[idx];
                            sumG += src[idx + 1];
                            sumR += src[idx + 2];
                            sumA += src[idx + 3];
                        }
                        int o = y * stride + col;
                        dst[o] = (byte)(sumB / window);
                        dst[o + 1] = (byte)(sumG / window);
                        dst[o + 2] = (byte)(sumR / window);
                        dst[o + 3] = (byte)(sumA / window);
                    }
                }
            }
        }
    }
}
