using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>Calendario compacto del mes actual.</summary>
    internal sealed class CalendarWidget : Grid
    {
        readonly TextBlock _month = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold };
        readonly TextBlock _year = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Right };
        readonly UniformGrid _head = new UniformGrid { Columns = 7 };
        readonly UniformGrid _days = new UniformGrid { Columns = 7, Margin = new Thickness(0, 2, 0, 0) };
        readonly DispatcherTimer _midnight = new DispatcherTimer();
        DateTime _built = DateTime.MinValue;
        bool _started;

        static CultureInfo C => CultureInfo.CurrentCulture;

        public CalendarWidget()
        {
            Width = 300;
            Height = 248;
            _month.SetResourceReference(TextBlock.ForegroundProperty, "TodayBrush");
            _year.SetResourceReference(TextBlock.ForegroundProperty, "GlassSubText");
            var header = new Grid { Margin = new Thickness(3, 0, 3, 8) };
            header.Children.Add(_month);
            header.Children.Add(_year);
            var stack = new StackPanel();
            stack.Children.Add(header);
            stack.Children.Add(_head);
            stack.Children.Add(_days);
            Children.Add(new GlassCard { CornerRadius = new CornerRadius(30), Padding = new Thickness(20, 16, 20, 12), Content = stack });
            _midnight.Tick += (s, e) => Build(true);
            Build(true);
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            Clockwork.TimeChanged += OnTime;
            Build(true);
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            Clockwork.TimeChanged -= OnTime;
            _midnight.Stop();
        }

        void OnTime() => Build(true);

        static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], C) + s.Substring(1);

        void Build(bool force)
        {
            var now = DateTime.Now;
            _midnight.Stop();
            _midnight.Interval = now.Date.AddDays(1) - now + TimeSpan.FromSeconds(2);
            if (_started) _midnight.Start();
            if (!force && _built == now.Date) return;
            _built = now.Date;

            _month.Text = Cap(now.ToString("MMMM", C));
            _year.Text = now.Year.ToString(C);
            var fdw = C.DateTimeFormat.FirstDayOfWeek;
            bool es = C.TwoLetterISOLanguageName == "es";

            _head.Children.Clear();
            for (int i = 0; i < 7; i++)
            {
                var dow = (DayOfWeek)(((int)fdw + i) % 7);
                string label = es ? "DLMXJVS"[(int)dow].ToString() : C.DateTimeFormat.GetShortestDayName(dow).Substring(0, 1).ToUpper(C);
                var tb = new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, Height = 18 };
                tb.SetResourceReference(TextBlock.ForegroundProperty, "GlassDim");
                _head.Children.Add(tb);
            }

            _days.Children.Clear();
            var first = new DateTime(now.Year, now.Month, 1);
            int offset = ((int)first.DayOfWeek - (int)fdw + 7) % 7;
            int days = DateTime.DaysInMonth(now.Year, now.Month);
            int cells = (int)Math.Ceiling((offset + days) / 7.0) * 7;
            double rowH = cells > 35 ? 25 : 29;
            for (int i = 0; i < cells; i++)
            {
                int day = i - offset + 1;
                var cell = new Grid { Height = rowH };
                if (day >= 1 && day <= days)
                {
                    var date = new DateTime(now.Year, now.Month, day);
                    bool today = date == now.Date;
                    bool weekend = date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
                    if (today)
                    {
                        var dot = new Ellipse { Width = 24, Height = 24 };
                        dot.SetResourceReference(Shape.FillProperty, "TodayBrush");
                        cell.Children.Add(dot);
                    }
                    var tb = new TextBlock
                    {
                        Text = day.ToString(C),
                        FontSize = 12,
                        FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    if (today) tb.Foreground = Brushes.White;
                    else tb.SetResourceReference(TextBlock.ForegroundProperty, weekend ? "GlassSubText" : "GlassText");
                    cell.Children.Add(tb);
                }
                _days.Children.Add(cell);
            }
        }
    }
}
