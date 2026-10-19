using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace WMac
{
    /// <summary>Clima real (Open-Meteo) de la ciudad configurada; si no hay ciudad, invita a configurarla.</summary>
    internal partial class WeatherWidget : UserControl
    {
        bool _started;

        public WeatherWidget()
        {
            InitializeComponent();
            SetupIcon.Set(WeatherKind.PartlyCloudy, true);
            SetupButton.Click += (s, e) => App.Instance?.ShowSettings();
            Render();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            WeatherService.Shared.Updated += Render;
            SettingsStore.Changed += Render;
            Render();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            WeatherService.Shared.Updated -= Render;
            SettingsStore.Changed -= Render;
        }

        static CultureInfo C => CultureInfo.CurrentCulture;
        static string T(double v) => Math.Round(v).ToString(C) + "°";

        void Render()
        {
            var s = SettingsStore.Current;
            var d = WeatherService.Shared.Current;
            if (!s.WeatherConfigured)
            {
                DataPanel.Visibility = Visibility.Collapsed;
                SetupPanel.Visibility = Visibility.Visible;
                SetupButton.Visibility = Visibility.Visible;
                SetupText.Text = "Añade tu ciudad para ver el pronóstico.";
                return;
            }
            if (d == null)
            {
                DataPanel.Visibility = Visibility.Collapsed;
                SetupPanel.Visibility = Visibility.Visible;
                SetupText.Text = WeatherService.Shared.Error != null
                    ? "Sin datos del clima por ahora."
                    : "Cargando el clima de " + s.WeatherPlace.Split(',')[0] + "…";
                SetupButton.Visibility = Visibility.Collapsed;
                return;
            }

            SetupButton.Visibility = Visibility.Visible;
            SetupPanel.Visibility = Visibility.Collapsed;
            DataPanel.Visibility = Visibility.Visible;
            PlaceText.Text = s.WeatherPlace.Split(',')[0];
            TempText.Text = T(d.Temp);
            MainIcon.Set(WeatherService.KindOf(d.Code), d.IsDay);
            CondText.Text = WeatherService.Describe(d.Code);
            RangeText.Text = $"Máx {T(d.Max)}   Mín {T(d.Min)}";

            HoursGrid.Children.Clear();
            foreach (var h in d.Hours)
            {
                var col = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
                var hour = new TextBlock { Text = h.Time.ToString("HH", C), FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center };
                hour.SetResourceReference(TextBlock.ForegroundProperty, "GlassSubText");
                var icon = new WeatherIcon { Width = 24, Height = 24, Margin = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Center };
                icon.Set(WeatherService.KindOf(h.Code), h.IsDay);
                var temp = new TextBlock { Text = T(h.Temp), FontSize = 12.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center };
                temp.SetResourceReference(TextBlock.ForegroundProperty, "GlassText");
                col.Children.Add(hour);
                col.Children.Add(icon);
                col.Children.Add(temp);
                HoursGrid.Children.Add(col);
            }

            DaysPanel.Children.Clear();
            foreach (var day in d.Days)
            {
                var row = new Grid { Height = 22 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
                string name = day.Date.Date == DateTime.Today.AddDays(1) ? "Mañana" : C.TextInfo.ToTitleCase(day.Date.ToString("dddd", C));
                var label = new TextBlock { Text = name, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.ForegroundProperty, "GlassText");
                var icon = new WeatherIcon { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
                icon.Set(WeatherService.KindOf(day.Code), true);
                Grid.SetColumn(icon, 1);
                var temps = new TextBlock { Text = $"{T(day.Min)}  {T(day.Max)}", FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
                temps.SetResourceReference(TextBlock.ForegroundProperty, "GlassSubText");
                Grid.SetColumn(temps, 2);
                row.Children.Add(label);
                row.Children.Add(icon);
                row.Children.Add(temps);
                DaysPanel.Children.Add(row);
            }
        }
    }
}
