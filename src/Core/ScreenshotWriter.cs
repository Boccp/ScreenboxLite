using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenboxLite.Core
{
    /// <summary>截图裁剪与保存。文件命名沿用 v1：“yy Md 当日秒数.png”，重名追加 (2)(3)…</summary>
    internal static class ScreenshotWriter
    {
        private static readonly object NameLock = new object();

        /// <summary>水平镜像翻转位图（无损逐行像素镜像，返回冻结的 Bgr32 位图）。</summary>
        public static BitmapSource FlipHorizontal(BitmapSource src)
        {
            if (src == null) return null;
            BitmapSource bgr = src.Format == PixelFormats.Bgr32
                ? src
                : new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0);
            int w = bgr.PixelWidth, h = bgr.PixelHeight;
            if (w <= 0 || h <= 0) return src;
            int stride = w * 4;
            int[] pixels = new int[w * h];
            bgr.CopyPixels(pixels, stride, 0);
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                int l = row, r = row + w - 1;
                while (l < r)
                {
                    int tmp = pixels[l];
                    pixels[l] = pixels[r];
                    pixels[r] = tmp;
                    l++;
                    r--;
                }
            }
            var flipped = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
            flipped.Freeze();
            return flipped;
        }

        /// <summary>按归一化矩形裁剪（null 表示整帧）。返回冻结的位图。</summary>
        public static BitmapSource Crop(BitmapSource frame, Rect? normalized)
        {
            if (frame == null || !normalized.HasValue) return frame;
            Rect c = normalized.Value;
            int fw = frame.PixelWidth, fh = frame.PixelHeight;
            int x = Clamp((int)Math.Round(c.X * fw), 0, fw - 1);
            int y = Clamp((int)Math.Round(c.Y * fh), 0, fh - 1);
            int w = Clamp((int)Math.Round(c.Width * fw), 1, fw - x);
            int h = Clamp((int)Math.Round(c.Height * fh), 1, fh - y);
            var cropped = new CroppedBitmap(frame, new Int32Rect(x, y, w, h));
            cropped.Freeze();
            return cropped;
        }

        /// <summary>立即分配文件名（占位防重名），后台编码写入 PNG。onError 在后台线程回调。</summary>
        public static string Save(BitmapSource bitmap, string folder, Action<Exception> onError)
        {
            Directory.CreateDirectory(folder);
            DateTime now = DateTime.Now;
            string baseName = string.Format("{0} {1}{2} {3}", now.ToString("yy"), now.Month, now.Day, (int)now.TimeOfDay.TotalSeconds);
            string path;
            lock (NameLock)
            {
                path = Path.Combine(folder, baseName + ".png");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, baseName + " (" + i + ").png");
                using (File.Create(path)) { }
            }

            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    using (Perf.Measure("shot.encode"))
                    using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        var enc = new PngBitmapEncoder();
                        enc.Frames.Add(BitmapFrame.Create(bitmap));
                        enc.Save(fs);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn(ex, "截图写入失败 " + path);
                    if (onError != null) onError(ex);
                }
            });
            return path;
        }

        private static int Clamp(int v, int lo, int hi)
        {
            return v < lo ? lo : (v > hi ? hi : v);
        }
    }
}
