using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WMac
{
    internal enum WallpaperFit { Center = 0, Tile = 1, Stretch = 2, Fit = 3, Fill = 4, Span = 5 }

    internal sealed class WallpaperSpec
    {
        public string Path;
        public WallpaperFit Fit = WallpaperFit.Fill;
        public Color Background = Color.FromRgb(0, 0, 0);
        public string Key => $"{Path}|{Fit}|{Background}";
    }

    internal sealed class WallpaperBitmaps
    {
        public WallpaperSpec Spec;
        public BitmapSource Image;
        public int OrigW, OrigH;
        public Rect Dest;            // píxeles relativos a la superficie
        public BitmapSource Blur;    // versión desenfocada a 1/4 para el efecto glass
        public BitmapSource Soft;    // fondo con desenfoque suave a 1/2 (lanzador)
        public double WidgetLuma = 0.3;
    }

    /// <summary>Lee el fondo de pantalla actual. Nunca lo modifica.</summary>
    internal static class WallpaperInfo
    {
        public static string TranscodedPath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");

        /// <summary>Fondo por monitor (misma posición en la lista que <paramref name="monitors"/>).</summary>
        public static List<WallpaperSpec> Query(List<MonitorInfo> monitors)
        {
            var fallback = FromRegistry();
            var result = monitors.Select(m => new WallpaperSpec { Path = fallback.Path, Fit = fallback.Fit, Background = fallback.Background }).ToList();
            IDesktopWallpaper dw = null;
            try
            {
                var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"));
                dw = (IDesktopWallpaper)Activator.CreateInstance(type);
                int pos = (int)fallback.Fit;
                if (dw.GetPosition(out int p) == 0) pos = p;
                Color bg = fallback.Background;
                if (dw.GetBackgroundColor(out uint c) == 0) bg = Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
                if (dw.GetMonitorDevicePathCount(out uint count) == 0)
                {
                    for (uint i = 0; i < count; i++)
                    {
                        if (dw.GetMonitorDevicePathAt(i, out IntPtr pid) != 0 || pid == IntPtr.Zero) continue;
                        string id = Marshal.PtrToStringUni(pid);
                        Native.CoTaskMemFree(pid);
                        if (dw.GetMonitorRECT(id, out Native.RECT rect) != 0) continue;
                        int idx = monitors.FindIndex(m => m.Bounds.Left == rect.Left && m.Bounds.Top == rect.Top && m.Bounds.Right == rect.Right && m.Bounds.Bottom == rect.Bottom);
                        if (idx < 0) continue;
                        string path = null;
                        if (dw.GetWallpaper(id, out IntPtr pw) == 0 && pw != IntPtr.Zero)
                        {
                            path = Marshal.PtrToStringUni(pw);
                            Native.CoTaskMemFree(pw);
                        }
                        result[idx] = new WallpaperSpec { Path = string.IsNullOrWhiteSpace(path) ? fallback.Path : path, Fit = (WallpaperFit)Math.Max(0, Math.Min(5, pos)), Background = bg };
                    }
                }
            }
            catch (Exception ex) { Log.Error("wallpaper query", ex); }
            finally { if (dw != null) Marshal.ReleaseComObject(dw); }
            return result;
        }

        static WallpaperSpec FromRegistry()
        {
            var spec = new WallpaperSpec();
            try
            {
                var sb = new StringBuilder(1024);
                if (Native.SystemParametersInfo(Native.SPI_GETDESKWALLPAPER, sb.Capacity, sb, 0)) spec.Path = sb.ToString();
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                {
                    string style = k?.GetValue("WallpaperStyle") as string ?? "10";
                    string tile = k?.GetValue("TileWallpaper") as string ?? "0";
                    spec.Fit = tile == "1" ? WallpaperFit.Tile
                        : style == "0" ? WallpaperFit.Center
                        : style == "2" ? WallpaperFit.Stretch
                        : style == "6" ? WallpaperFit.Fit
                        : style == "22" ? WallpaperFit.Span
                        : WallpaperFit.Fill;
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors"))
                {
                    var parts = (k?.GetValue("Background") as string ?? "").Split(' ');
                    if (parts.Length == 3 && byte.TryParse(parts[0], out byte r) && byte.TryParse(parts[1], out byte g) && byte.TryParse(parts[2], out byte b))
                        spec.Background = Color.FromRgb(r, g, b);
                    else
                    {
                        int c = Native.GetSysColor(Native.COLOR_DESKTOP);
                        spec.Background = Color.FromRgb((byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
                    }
                }
            }
            catch (Exception ex) { Log.Error("wallpaper registry", ex); }
            return spec;
        }

        /// <summary>Firma barata para detectar cambios de fondo (incluye presentaciones y Spotlight).</summary>
        public static string QuickSignature()
        {
            var spec = FromRegistry();
            string t = "";
            try { if (File.Exists(TranscodedPath)) t = File.GetLastWriteTimeUtc(TranscodedPath).Ticks.ToString(); } catch { }
            return spec.Key + "|" + t;
        }
    }

    internal static class WallpaperRenderer
    {
        const int BlurDownscale = 4;

        public static void LoadAsync(WallpaperSpec spec, Native.RECT surface, Native.RECT virtualBounds, Rect widgetRegionPx,
                                     Dispatcher ui, Action<WallpaperBitmaps> done)
        {
            var t = new Thread(() =>
            {
                WallpaperBitmaps result = null;
                try { result = Load(spec, surface, virtualBounds, widgetRegionPx); }
                catch (Exception ex) { Log.Error("wallpaper load", ex); }
                if (result == null) result = new WallpaperBitmaps { Spec = spec };
                ui.BeginInvoke((Action)(() => done(result)));
            }) { IsBackground = true, Name = "WMac.Wallpaper" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        static byte[] TryRead(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length <= 0 || fs.Length > 200L * 1024 * 1024) return null;
                    var data = new byte[fs.Length];
                    int read = 0;
                    while (read < data.Length)
                    {
                        int n = fs.Read(data, read, data.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    return data;
                }
            }
            catch { return null; }
        }

        static WallpaperBitmaps Load(WallpaperSpec spec, Native.RECT surface, Native.RECT virt, Rect widgetRegion)
        {
            var res = new WallpaperBitmaps { Spec = spec };
            int W = Math.Max(1, surface.Width), H = Math.Max(1, surface.Height);

            BitmapImage image = null;
            if (!string.IsNullOrWhiteSpace(spec.Path))
            {
                image = Decode(TryRead(spec.Path), spec, surface, virt, res) ?? Decode(TryRead(WallpaperInfo.TranscodedPath), spec, surface, virt, res);
            }
            res.Image = image;
            BuildBlur(res, W, H, widgetRegion);
            return res;
        }

        static BitmapImage Decode(byte[] data, WallpaperSpec spec, Native.RECT surface, Native.RECT virt, WallpaperBitmaps res)
        {
            if (data == null) return null;
            try
            {
                int iw, ih;
                using (var ms = new MemoryStream(data))
                {
                    var dec = BitmapDecoder.Create(ms, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
                    iw = dec.Frames[0].PixelWidth;
                    ih = dec.Frames[0].PixelHeight;
                }
                if (iw <= 0 || ih <= 0) return null;
                var dest = ComputeDest(spec.Fit, iw, ih, surface, virt);

                int dw = 0, dh = 0;
                switch (spec.Fit)
                {
                    case WallpaperFit.Fill:
                    case WallpaperFit.Fit:
                    case WallpaperFit.Span:
                        if (dest.Width < iw) dw = (int)Math.Ceiling(dest.Width);
                        break;
                    case WallpaperFit.Stretch:
                        dw = Math.Min(iw, surface.Width);
                        dh = Math.Min(ih, surface.Height);
                        break;
                    default:
                        if (iw > 8192) dw = 8192;
                        break;
                }

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.StreamSource = new MemoryStream(data);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                if (dw > 0) bi.DecodePixelWidth = dw;
                if (dh > 0) bi.DecodePixelHeight = dh;
                bi.EndInit();
                bi.Freeze();

                res.OrigW = iw; res.OrigH = ih; res.Dest = dest;
                return bi;
            }
            catch (Exception ex)
            {
                Log.Error("wallpaper decode", ex);
                return null;
            }
        }

        static Rect ComputeDest(WallpaperFit fit, int iw, int ih, Native.RECT surface, Native.RECT virt)
        {
            double W = surface.Width, H = surface.Height;
            switch (fit)
            {
                case WallpaperFit.Stretch:
                    return new Rect(0, 0, W, H);
                case WallpaperFit.Fit:
                {
                    double s = Math.Min(W / iw, H / ih), w = iw * s, h = ih * s;
                    return new Rect((W - w) / 2, (H - h) / 2, w, h);
                }
                case WallpaperFit.Center:
                    return new Rect(Math.Round((W - iw) / 2), Math.Round((H - ih) / 2), iw, ih);
                case WallpaperFit.Tile:
                    return new Rect(0, 0, iw, ih);
                case WallpaperFit.Span:
                {
                    double VW = virt.Width, VH = virt.Height;
                    double s = Math.Max(VW / iw, VH / ih), w = iw * s, h = ih * s;
                    double x = virt.Left + (VW - w) / 2 - surface.Left, y = virt.Top + (VH - h) / 2 - surface.Top;
                    return new Rect(x, y, w, h);
                }
                default:
                {
                    double s = Math.Max(W / iw, H / ih), w = iw * s, h = ih * s;
                    return new Rect((W - w) / 2, (H - h) / 2, w, h);
                }
            }
        }

        // ---------- desenfoque (glass) ----------

        static void BuildBlur(WallpaperBitmaps res, int W, int H, Rect widgetRegion)
        {
            // Fondo del lanzador: desenfoque suave a media resolución.
            var soft = Composite(res, W, H, 2, out int sw, out int sh);
            BoxBlur(soft, sw, sh, 4, 3);
            Saturate(soft, 1.1);
            var softBmp = BitmapSource.Create(sw, sh, 96, 96, PixelFormats.Bgra32, null, soft, sw * 4);
            softBmp.Freeze();
            res.Soft = softBmp;

            // Cristal de las tarjetas: desenfoque fuerte a 1/4.
            var buf = Composite(res, W, H, BlurDownscale, out int bw, out int bh);
            double f = bw / (double)W;
            BoxBlur(buf, bw, bh, 6, 3);
            Saturate(buf, 1.22);

            // Luminancia media de la zona de widgets → decide cristal claro u oscuro.
            int x0 = Clamp((int)(widgetRegion.X * f), 0, bw - 1), y0 = Clamp((int)(widgetRegion.Y * f), 0, bh - 1);
            int x1 = Clamp((int)(widgetRegion.Right * f), x0 + 1, bw), y1 = Clamp((int)(widgetRegion.Bottom * f), y0 + 1, bh);
            double sum = 0; long n = 0;
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = (y * bw + x) * 4;
                    sum += (0.0722 * buf[i] + 0.7152 * buf[i + 1] + 0.2126 * buf[i + 2]) / 255.0;
                    n++;
                }
            res.WidgetLuma = n > 0 ? sum / n : 0.3;

            var bmp = BitmapSource.Create(bw, bh, 96, 96, PixelFormats.Bgra32, null, buf, bw * 4);
            bmp.Freeze();
            res.Blur = bmp;
        }

        /// <summary>Compone fondo (color + imagen colocada según el modo) a 1/<paramref name="down"/> de resolución.</summary>
        static byte[] Composite(WallpaperBitmaps res, int W, int H, int down, out int bw, out int bh)
        {
            bw = Math.Max(16, W / down);
            bh = Math.Max(16, H / down);
            double f = bw / (double)W;
            var buf = new byte[bw * bh * 4];
            var bg = res.Spec.Background;
            for (int i = 0; i < buf.Length; i += 4) { buf[i] = bg.B; buf[i + 1] = bg.G; buf[i + 2] = bg.R; buf[i + 3] = 255; }

            if (res.Image != null)
            {
                if (res.Spec.Fit == WallpaperFit.Tile)
                {
                    int tw = Math.Max(1, (int)Math.Round(res.OrigW * f)), th = Math.Max(1, (int)Math.Round(res.OrigH * f));
                    var tile = ScaledPixels(res.Image, tw, th, out tw, out th);
                    for (int y = 0; y < bh; y += th)
                        for (int x = 0; x < bw; x += tw)
                            Blit(buf, bw, bh, tile, tw, th, x, y);
                }
                else
                {
                    var d = res.Dest;
                    int dw = Math.Max(1, (int)Math.Round(d.Width * f)), dh = Math.Max(1, (int)Math.Round(d.Height * f));
                    var px = ScaledPixels(res.Image, dw, dh, out dw, out dh);
                    Blit(buf, bw, bh, px, dw, dh, (int)Math.Round(d.X * f), (int)Math.Round(d.Y * f));
                }
            }
            return buf;
        }

        static byte[] ScaledPixels(BitmapSource src, int w, int h, out int outW, out int outH)
        {
            BitmapSource s = new TransformedBitmap(src, new ScaleTransform(w / (double)src.PixelWidth, h / (double)src.PixelHeight));
            s = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
            outW = s.PixelWidth; outH = s.PixelHeight;
            var px = new byte[outW * outH * 4];
            s.CopyPixels(px, outW * 4, 0);
            return px;
        }

        static void Blit(byte[] dst, int dw, int dh, byte[] src, int sw, int sh, int ox, int oy)
        {
            int x0 = Math.Max(0, ox), y0 = Math.Max(0, oy), x1 = Math.Min(dw, ox + sw), y1 = Math.Min(dh, oy + sh);
            if (x1 <= x0 || y1 <= y0) return;
            int len = (x1 - x0) * 4;
            for (int y = y0; y < y1; y++)
                Buffer.BlockCopy(src, ((y - oy) * sw + (x0 - ox)) * 4, dst, (y * dw + x0) * 4, len);
            for (int y = y0; y < y1; y++) for (int x = x0; x < x1; x++) dst[(y * dw + x) * 4 + 3] = 255;
        }

        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        static void BoxBlur(byte[] px, int w, int h, int r, int passes)
        {
            var tmp = new byte[px.Length];
            for (int p = 0; p < passes; p++)
            {
                BlurH(px, tmp, w, h, r);
                BlurV(tmp, px, w, h, r);
            }
        }

        static void BlurH(byte[] src, byte[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * w * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[row + Clamp(i, 0, w - 1) * 4 + c];
                    for (int x = 0; x < w; x++)
                    {
                        dst[row + x * 4 + c] = (byte)(sum / div);
                        sum += src[row + Clamp(x + r + 1, 0, w - 1) * 4 + c] - src[row + Clamp(x - r, 0, w - 1) * 4 + c];
                    }
                }
                for (int x = 0; x < w; x++) dst[row + x * 4 + 3] = 255;
            }
        }

        static void BlurV(byte[] src, byte[] dst, int w, int h, int r)
        {
            int div = 2 * r + 1, stride = w * 4;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                for (int c = 0; c < 3; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += src[Clamp(i, 0, h - 1) * stride + col + c];
                    for (int y = 0; y < h; y++)
                    {
                        dst[y * stride + col + c] = (byte)(sum / div);
                        sum += src[Clamp(y + r + 1, 0, h - 1) * stride + col + c] - src[Clamp(y - r, 0, h - 1) * stride + col + c];
                    }
                }
                for (int y = 0; y < h; y++) dst[y * stride + col + 3] = 255;
            }
        }

        static void Saturate(byte[] px, double s)
        {
            for (int i = 0; i < px.Length; i += 4)
            {
                double b = px[i], g = px[i + 1], r = px[i + 2];
                double l = 0.0722 * b + 0.7152 * g + 0.2126 * r;
                px[i] = (byte)Math.Max(0, Math.Min(255, l + (b - l) * s));
                px[i + 1] = (byte)Math.Max(0, Math.Min(255, l + (g - l) * s));
                px[i + 2] = (byte)Math.Max(0, Math.Min(255, l + (r - l) * s));
            }
        }

        // ---------- capa visual ----------

        public static FrameworkElement BuildLayer(WallpaperBitmaps bm, double widthPx, double heightPx, double pxPerDip, bool soft = false)
        {
            var bg = new SolidColorBrush(bm.Spec.Background);
            bg.Freeze();
            var canvas = new Canvas
            {
                Width = widthPx / pxPerDip,
                Height = heightPx / pxPerDip,
                Background = bg,
                ClipToBounds = true,
                IsHitTestVisible = false
            };
            if (soft && bm.Soft != null)
            {
                // La composición ya incluye posición y color: basta con escalarla a toda la superficie.
                var softImg = new Image { Source = bm.Soft, Stretch = Stretch.Fill, Width = canvas.Width, Height = canvas.Height };
                RenderOptions.SetBitmapScalingMode(softImg, BitmapScalingMode.Linear);
                canvas.Children.Add(softImg);
                return canvas;
            }
            if (bm.Image == null) return canvas;

            if (bm.Spec.Fit == WallpaperFit.Tile)
            {
                var brush = new ImageBrush(bm.Image)
                {
                    TileMode = TileMode.Tile,
                    ViewportUnits = BrushMappingMode.Absolute,
                    Viewport = new Rect(0, 0, bm.OrigW / pxPerDip, bm.OrigH / pxPerDip),
                    Stretch = Stretch.Fill
                };
                brush.Freeze();
                canvas.Children.Add(new Rectangle { Width = canvas.Width, Height = canvas.Height, Fill = brush });
            }
            else
            {
                var img = new Image
                {
                    Source = bm.Image,
                    Stretch = Stretch.Fill,
                    Width = bm.Dest.Width / pxPerDip,
                    Height = bm.Dest.Height / pxPerDip
                };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Canvas.SetLeft(img, bm.Dest.X / pxPerDip);
                Canvas.SetTop(img, bm.Dest.Y / pxPerDip);
                canvas.Children.Add(img);
            }
            return canvas;
        }
    }
}
