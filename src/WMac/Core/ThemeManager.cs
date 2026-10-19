using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace WMac
{
    /// <summary>Colores de menús y diálogos según el modo claro/oscuro y el color de énfasis de Windows (solo lectura).</summary>
    internal static class ThemeManager
    {
        public static bool AppsDark
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                        return !(k?.GetValue("AppsUseLightTheme") is int v) || v == 0;
                }
                catch { return true; }
            }
        }

        public static Color Accent
        {
            get
            {
                try
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                        if (k?.GetValue("AccentColor") is int abgr)
                        {
                            var c = Color.FromRgb((byte)(abgr & 0xFF), (byte)((abgr >> 8) & 0xFF), (byte)((abgr >> 16) & 0xFF));
                            // Acentos muy oscuros o muy claros se ven mal como selección: usar el azul de macOS.
                            double l = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                            if (l > 0.12 && l < 0.85) return c;
                        }
                }
                catch { }
                return Color.FromRgb(0x0A, 0x84, 0xFF);
            }
        }

        static SolidColorBrush B(uint argb)
        {
            var b = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            b.Freeze();
            return b;
        }

        public static void Apply()
        {
            var r = Application.Current.Resources;
            bool dark = AppsDark;
            var accent = Accent;
            var accentBrush = new SolidColorBrush(accent); accentBrush.Freeze();
            r["AccentColor"] = accent;
            r["AccentBrush"] = accentBrush;
            r["AccentSoftBrush"] = B(0x38000000u | ((uint)accent.R << 16) | ((uint)accent.G << 8) | accent.B);

            if (dark)
            {
                r["MenuBackground"] = B(0xF0232328);
                r["MenuBorder"] = B(0x33FFFFFF);
                r["MenuForeground"] = B(0xFFF2F2F7);
                r["MenuSubForeground"] = B(0x8CF2F2F7);
                r["MenuSeparator"] = B(0x1FFFFFFF);
                r["DialogTint"] = B(0x30101014);
                r["DialogFallback"] = B(0xFF1F1F24);
                r["DialogText"] = B(0xFFF5F5F7);
                r["DialogSubText"] = B(0xA6F5F5F7);
                r["DialogCard"] = B(0x14FFFFFF);
                r["DialogCardBorder"] = B(0x14FFFFFF);
                r["ControlBackground"] = B(0x1FFFFFFF);
                r["ControlHover"] = B(0x2EFFFFFF);
                r["ControlBorder"] = B(0x24FFFFFF);
                r["SwitchOff"] = B(0x40FFFFFF);
            }
            else
            {
                r["MenuBackground"] = B(0xF2F7F7F9);
                r["MenuBorder"] = B(0x1F000000);
                r["MenuForeground"] = B(0xFF1D1D1F);
                r["MenuSubForeground"] = B(0x8C1D1D1F);
                r["MenuSeparator"] = B(0x1A000000);
                r["DialogTint"] = B(0x40FFFFFF);
                r["DialogFallback"] = B(0xFFF3F3F5);
                r["DialogText"] = B(0xFF1D1D1F);
                r["DialogSubText"] = B(0x991D1D1F);
                r["DialogCard"] = B(0x8CFFFFFF);
                r["DialogCardBorder"] = B(0x14000000);
                r["ControlBackground"] = B(0xCCFFFFFF);
                r["ControlHover"] = B(0xFFFFFFFF);
                r["ControlBorder"] = B(0x1F000000);
                r["SwitchOff"] = B(0x33000000);
            }
        }
    }
}
