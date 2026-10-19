using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace WMac
{
    /// <summary>Fondo acrílico/Mica nativo de Windows 11 para las ventanas de WMac (con alternativa sólida).</summary>
    internal static class GlassWindow
    {
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33,
                  DWMWA_SYSTEMBACKDROP_TYPE = 38, DWMWA_MICA_EFFECT = 1029;

        public static void Apply(Window w, bool acrylic = true)
        {
            w.SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                bool ok = false;
                try
                {
                    int dark = ThemeManager.AppsDark ? 1 : 0;
                    Native.DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
                    int round = 2;
                    Native.DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
                    int build = Environment.OSVersion.Version.Build;
                    if (build >= 22621)
                    {
                        int type = acrylic ? 3 : 2;
                        ok = Native.DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, 4) == 0;
                    }
                    else if (build >= 22000)
                    {
                        int on = 1;
                        ok = Native.DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref on, 4) == 0;
                    }
                    if (ok)
                    {
                        var src = HwndSource.FromHwnd(hwnd);
                        if (src?.CompositionTarget != null) src.CompositionTarget.BackgroundColor = Colors.Transparent;
                        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                        Native.DwmExtendFrameIntoClientArea(hwnd, ref m);
                        w.Background = Brushes.Transparent;
                    }
                }
                catch (Exception ex) { Log.Error("backdrop", ex); }
                if (!ok) w.SetResourceReference(Window.BackgroundProperty, "DialogFallback");
            };
        }
    }
}
