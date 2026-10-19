using System;
using System.Windows;
using System.Windows.Media;

namespace WMac
{
    /// <summary>Iconos meteorológicos vectoriales dibujados a mano (sin recursos externos).</summary>
    internal sealed class WeatherIcon : FrameworkElement
    {
        WeatherKind _kind = WeatherKind.Clear;
        bool _day = true;

        static readonly Brush Sun = Frozen(new RadialGradientBrush(Color.FromRgb(0xFF, 0xE0, 0x6B), Color.FromRgb(0xFF, 0xA9, 0x2E)));
        static readonly Brush Moon = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xF4, 0xD6), Color.FromRgb(0xE6, 0xD3, 0x9E), 90));
        static readonly Brush Cloud = Frozen(new LinearGradientBrush(Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromRgb(0xD9, 0xE2, 0xEC), 90));
        static readonly Brush DarkCloud = Frozen(new LinearGradientBrush(Color.FromRgb(0xC9, 0xD1, 0xDB), Color.FromRgb(0x8E, 0x99, 0xA8), 90));
        static readonly Pen RayPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0xFF, 0xC4, 0x3D)), 2.2);
        static readonly Pen RainPen = FrozenPen(new SolidColorBrush(Color.FromRgb(0x64, 0xB5, 0xF6)), 2.2);
        static readonly Pen FogPen = FrozenPen(new SolidColorBrush(Color.FromArgb(0xE0, 0xE3, 0xE8, 0xEE)), 2.6);
        static readonly Brush Bolt = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xD6, 0x0A)));
        static readonly Brush Snow = Frozen(new SolidColorBrush(Colors.White));

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

        public void Set(WeatherKind kind, bool day)
        {
            _kind = kind; _day = day;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double s = Math.Min(ActualWidth, ActualHeight) / 40.0;
            if (s <= 0) return;
            dc.PushTransform(new ScaleTransform(s, s));
            switch (_kind)
            {
                case WeatherKind.Clear:
                    if (_day) DrawSun(dc, 20, 20, 8.5, true); else DrawMoon(dc, 20, 20, 11);
                    break;
                case WeatherKind.PartlyCloudy:
                    if (_day) DrawSun(dc, 14, 14, 6.5, true); else DrawMoon(dc, 14, 13, 8);
                    DrawCloud(dc, 7, 15, 1.0, Cloud);
                    break;
                case WeatherKind.Cloudy:
                    DrawCloud(dc, 2, 6, 0.75, DarkCloud);
                    DrawCloud(dc, 6, 12, 1.0, Cloud);
                    break;
                case WeatherKind.Fog:
                    DrawCloud(dc, 4, 4, 0.95, Cloud);
                    dc.DrawLine(FogPen, new Point(8, 30), new Point(32, 30));
                    dc.DrawLine(FogPen, new Point(5, 35), new Point(27, 35));
                    break;
                case WeatherKind.Drizzle:
                case WeatherKind.Rain:
                    DrawCloud(dc, 4, 3, 0.95, _kind == WeatherKind.Rain ? DarkCloud : Cloud);
                    int drops = _kind == WeatherKind.Rain ? 3 : 2;
                    for (int i = 0; i < drops; i++)
                    {
                        double x = 13 + i * 7;
                        dc.DrawLine(RainPen, new Point(x, 28), new Point(x - 2.5, 35));
                    }
                    break;
                case WeatherKind.Snow:
                    DrawCloud(dc, 4, 3, 0.95, Cloud);
                    for (int i = 0; i < 3; i++) dc.DrawEllipse(Snow, null, new Point(13 + i * 7, 31 + (i % 2) * 3), 2, 2);
                    break;
                case WeatherKind.Storm:
                    DrawCloud(dc, 4, 2, 0.95, DarkCloud);
                    var bolt = Geometry.Parse("M 21,24 L 15,33 L 20,33 L 17,40 L 26,29 L 21,29 L 24,24 Z");
                    dc.DrawGeometry(Bolt, null, bolt);
                    break;
            }
            dc.Pop();
        }

        static void DrawSun(DrawingContext dc, double cx, double cy, double r, bool rays)
        {
            if (rays)
            {
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    dc.DrawLine(RayPen, new Point(cx + Math.Cos(a) * (r + 3), cy + Math.Sin(a) * (r + 3)),
                                        new Point(cx + Math.Cos(a) * (r + 6.5), cy + Math.Sin(a) * (r + 6.5)));
                }
            }
            dc.DrawEllipse(Sun, null, new Point(cx, cy), r, r);
        }

        static void DrawMoon(DrawingContext dc, double cx, double cy, double r)
        {
            var g = new CombinedGeometry(GeometryCombineMode.Exclude,
                new EllipseGeometry(new Point(cx, cy), r, r),
                new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.35), r * 0.85, r * 0.85));
            dc.DrawGeometry(Moon, null, g);
        }

        static void DrawCloud(DrawingContext dc, double x, double y, double k, Brush fill)
        {
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };
            group.Children.Add(new EllipseGeometry(new Point(x + 10 * k, y + 15 * k), 7 * k, 7 * k));
            group.Children.Add(new EllipseGeometry(new Point(x + 19 * k, y + 11 * k), 9 * k, 9 * k));
            group.Children.Add(new EllipseGeometry(new Point(x + 27 * k, y + 16 * k), 6.5 * k, 6.5 * k));
            group.Children.Add(new RectangleGeometry(new Rect(x + 10 * k, y + 14 * k, 17 * k, 8.5 * k), 4 * k, 4 * k));
            dc.DrawGeometry(fill, null, group);
        }
    }
}
