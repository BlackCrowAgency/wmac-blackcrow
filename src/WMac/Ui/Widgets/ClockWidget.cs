using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>Avisos globales de cambio de hora del sistema (zona horaria, reanudación…).</summary>
    internal static class Clockwork
    {
        public static event Action TimeChanged;
        public static void RaiseTimeChanged() => TimeChanged?.Invoke();
    }

    /// <summary>Reloj analógico grande sobre cristal claro.</summary>
    internal sealed class ClockWidget : Grid
    {
        readonly ClockFace _face = new ClockFace();
        readonly DispatcherTimer _timer = new DispatcherTimer(DispatcherPriority.Normal);
        bool _started;

        public ClockWidget()
        {
            Width = Height = 300;
            var card = new GlassCard { CornerRadius = new CornerRadius(150), Padding = new Thickness(0), Content = _face };
            // Esfera clara: sobrescribe los pinceles del cristal solo en esta tarjeta.
            card.Resources["GlassTint"] = Frozen(new SolidColorBrush(Color.FromArgb(0xC2, 0xF4, 0xF4, 0xF7)));
            card.Resources["GlassSheen"] = Frozen(new LinearGradientBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 90));
            card.Resources["GlassRim"] = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)));
            card.Resources["GlassInnerRim"] = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
            Children.Add(card);
            Cursor = Cursors.Hand;
            ToolTip = "Abrir Reloj de Windows";
            MouseLeftButtonUp += (s, e) => ShellOps.OpenUri("ms-clock:");
            _timer.Tick += (s, e) => Tick();
        }

        static Brush Frozen(Brush b) { b.Freeze(); return b; }

        public void Start()
        {
            if (_started) return;
            _started = true;
            Clockwork.TimeChanged += Tick;
            SettingsStore.Changed += Tick;
            Tick();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            Clockwork.TimeChanged -= Tick;
            SettingsStore.Changed -= Tick;
            _timer.Stop();
        }

        void Tick()
        {
            var now = DateTime.Now;
            _face.Update(now, SettingsStore.Current.ShowSeconds, SettingsStore.Current.ShowDate);
            var next = SettingsStore.Current.ShowSeconds
                ? TimeSpan.FromMilliseconds(1000 - now.Millisecond + 10)
                : TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond) + TimeSpan.FromMilliseconds(30);
            _timer.Stop();
            _timer.Interval = next < TimeSpan.FromMilliseconds(40) ? TimeSpan.FromMilliseconds(40) : next;
            if (_started) _timer.Start();
        }
    }

    internal sealed class ClockFace : FrameworkElement
    {
        static readonly Brush Ink = Frozen(new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)));
        static readonly Brush Soft = Frozen(new SolidColorBrush(Color.FromRgb(0x8E, 0x8E, 0x93)));
        static readonly Brush Second = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x95, 0x00)));
        static readonly Brush Red = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)));
        static readonly Pen HourTick = FrozenPen(Ink, 2.6);
        static readonly Pen MinuteTick = FrozenPen(Soft, 1.1);
        static readonly Typeface Numbers = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        static readonly Typeface Small = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);

        DateTime _now = DateTime.Now;
        bool _seconds, _date;

        static Brush Frozen(Brush b) { b.Freeze(); return b; }
        static Pen FrozenPen(Brush b, double w) { var p = new Pen(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; p.Freeze(); return p; }

        public void Update(DateTime now, bool seconds, bool date)
        {
            _now = now; _seconds = seconds; _date = date;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0) return;
            var c = new Point(ActualWidth / 2, ActualHeight / 2);
            double R = size * 0.43;
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            for (int i = 0; i < 60; i++)
            {
                bool hour = i % 5 == 0;
                double a = i * Math.PI / 30;
                double r1 = R, r0 = hour ? R - size * 0.04 : R - size * 0.02;
                dc.DrawLine(hour ? HourTick : MinuteTick, P(c, a, r0), P(c, a, r1));
            }

            for (int h = 1; h <= 12; h++)
            {
                var ft = new FormattedText(h.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                           Numbers, size * 0.072, Ink, ppd);
                var p = P(c, h * Math.PI / 6, R - size * 0.105);
                dc.DrawText(ft, new Point(p.X - ft.Width / 2, p.Y - ft.Height / 2));
            }

            if (_date)
            {
                var culture = CultureInfo.CurrentCulture;
                string day = culture.DateTimeFormat.GetAbbreviatedDayName(_now.DayOfWeek).TrimEnd('.').ToUpper(culture);
                var ftDay = new FormattedText(day, culture, FlowDirection.LeftToRight, Small, size * 0.045, Red, ppd);
                var ftNum = new FormattedText(_now.Day.ToString(culture), culture, FlowDirection.LeftToRight, Small, size * 0.045, Ink, ppd);
                double w = ftDay.Width + size * 0.015 + ftNum.Width, y = c.Y + R * 0.38;
                dc.DrawText(ftDay, new Point(c.X - w / 2, y));
                dc.DrawText(ftNum, new Point(c.X - w / 2 + ftDay.Width + size * 0.015, y));
            }

            double sec = _now.Second, min = _now.Minute + (_seconds ? sec / 60.0 : 0), hr = _now.Hour % 12 + min / 60.0;
            Hand(dc, c, hr * Math.PI / 6, R * 0.52, size * 0.024, Ink);
            Hand(dc, c, min * Math.PI / 30, R * 0.82, size * 0.017, Ink);
            dc.DrawEllipse(Ink, null, c, size * 0.022, size * 0.022);
            if (_seconds)
            {
                double a = sec * Math.PI / 30;
                var pen = new Pen(Second, size * 0.006) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(pen, P(c, a + Math.PI, R * 0.16), P(c, a, R * 0.92));
                dc.DrawEllipse(Second, null, c, size * 0.016, size * 0.016);
            }
            dc.DrawEllipse(Brushes.White, null, c, size * 0.007, size * 0.007);
        }

        static void Hand(DrawingContext dc, Point c, double angle, double length, double width, Brush b)
        {
            var pen = new Pen(b, width) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, P(c, angle + Math.PI, length * 0.12), P(c, angle, length));
        }

        static Point P(Point c, double angle, double r) => new Point(c.X + Math.Sin(angle) * r, c.Y - Math.Cos(angle) * r);
    }
}
