using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WMac
{
    /// <summary>Extrae iconos y miniaturas reales del Shell (solo lectura).</summary>
    internal static class ShellIcons
    {
        const int SIIGBF_RESIZETOFIT = 0x00, SIIGBF_ICONONLY = 0x04;
        static Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        public static BitmapSource Get(string parsingName, int px, bool allowThumbnail)
        {
            IntPtr unk = IntPtr.Zero, hbmp = IntPtr.Zero;
            try
            {
                if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItem, out unk) != 0 || unk == IntPtr.Zero)
                    return null;
                var factory = Marshal.GetObjectForIUnknown(unk) as IShellItemImageFactory;
                if (factory == null) return null;
                try
                {
                    var size = new SIZE { cx = px, cy = px };
                    int hr = factory.GetImage(size, allowThumbnail ? SIIGBF_RESIZETOFIT : SIIGBF_ICONONLY, out hbmp);
                    if ((hr != 0 || hbmp == IntPtr.Zero) && allowThumbnail)
                        hr = factory.GetImage(size, SIIGBF_ICONONLY, out hbmp);
                    if (hr != 0 || hbmp == IntPtr.Zero) return null;
                    return FromHBitmap(hbmp);
                }
                finally { Marshal.ReleaseComObject(factory); }
            }
            catch (Exception ex)
            {
                Log.Error("icon " + parsingName, ex);
                return null;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) Native.DeleteObject(hbmp);
                if (unk != IntPtr.Zero) Marshal.Release(unk);
            }
        }

        public static string GetDisplayName(string parsingName)
        {
            IntPtr unk = IntPtr.Zero;
            try
            {
                if (Native.SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref IID_IShellItem, out unk) != 0 || unk == IntPtr.Zero)
                    return null;
                var item = Marshal.GetObjectForIUnknown(unk) as IShellItem;
                if (item == null) return null;
                try
                {
                    if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out IntPtr psz) != 0 || psz == IntPtr.Zero) return null;
                    string s = Marshal.PtrToStringUni(psz);
                    Native.CoTaskMemFree(psz);
                    return s;
                }
                finally { Marshal.ReleaseComObject(item); }
            }
            catch { return null; }
            finally { if (unk != IntPtr.Zero) Marshal.Release(unk); }
        }

        /// <summary>Convierte un HBITMAP de 32 bpp (alfa premultiplicado) a BitmapSource conservando la transparencia.</summary>
        static BitmapSource FromHBitmap(IntPtr hbmp)
        {
            var ds = new Native.DIBSECTION();
            int got = Native.GetObject(hbmp, Marshal.SizeOf(typeof(Native.DIBSECTION)), ref ds);
            if (got == 0 || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero)
            {
                var fallback = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbmp, IntPtr.Zero,
                    System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                fallback.Freeze();
                return fallback;
            }

            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var buf = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);

            // DIB ascendente (biHeight > 0): invertir filas.
            if (ds.dsBmih.biHeight > 0)
            {
                var row = new byte[stride];
                for (int y = 0; y < h / 2; y++)
                {
                    int a = y * stride, b = (h - 1 - y) * stride;
                    Buffer.BlockCopy(buf, a, row, 0, stride);
                    Buffer.BlockCopy(buf, b, buf, a, stride);
                    Buffer.BlockCopy(row, 0, buf, b, stride);
                }
            }

            // Iconos antiguos sin canal alfa: hacerlos opacos.
            bool anyAlpha = false;
            for (int i = 3; i < buf.Length; i += 4) if (buf[i] != 0) { anyAlpha = true; break; }
            if (!anyAlpha) for (int i = 3; i < buf.Length; i += 4) buf[i] = 255;

            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }

        public static bool IsRecycleBinEmpty()
        {
            try
            {
                var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                return Native.SHQueryRecycleBin(null, ref info) != 0 || info.i64NumItems == 0;
            }
            catch { return true; }
        }
    }
}
