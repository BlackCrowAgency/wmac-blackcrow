using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WMac
{
    internal partial class SettingsWindow : Window
    {
        bool _loading;

        public SettingsWindow()
        {
            InitializeComponent();
            GlassWindow.Apply(this);
            Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/WMac;component/Assets/WMac.ico"));

            Refresh();

            DesktopSwitch.Click += (s, e) =>
            {
                if (DesktopSwitch.IsChecked == true) App.Instance?.ActivateDesktop();
                else App.Instance?.DeactivateDesktop();
            };
            PreviewButton.Click += (s, e) => App.Instance?.ShowPreview();

            SizeSmall.Checked += (s, e) => Change(x => x.IconSize = "small");
            SizeMedium.Checked += (s, e) => Change(x => x.IconSize = "medium");
            SizeLarge.Checked += (s, e) => Change(x => x.IconSize = "large");
            SizeXLarge.Checked += (s, e) => Change(x => x.IconSize = "xlarge");
            Bind(IconsSwitch, (x, v) => x.ShowIcons = v);
            Bind(ClockSwitch, (x, v) => x.ShowClock = v);
            Bind(SecondsSwitch, (x, v) => x.ShowSeconds = v);
            Bind(DateSwitch, (x, v) => x.ShowDate = v);
            Bind(CalendarSwitch, (x, v) => x.ShowCalendar = v);
            Bind(WeatherSwitch, (x, v) => { x.ShowWeather = v; WeatherService.Shared.Refresh(); });
            Bind(MusicSwitch, (x, v) => x.ShowMusic = v);
            Bind(ProtectSwitch, (x, v) => x.ProtectFiles = v);
            CheckButton.Click += (s, e) => App.Instance?.CheckDesktopNow();
            BackupButton.Click += (s, e) => App.Instance?.BackupNow();
            BackupFolderButton.Click += (s, e) => { if (System.IO.Directory.Exists(LayoutBackups.Dir)) ShellOps.OpenFolder(LayoutBackups.Dir); };
            OrganizeButton.Click += (s, e) => MessageDialog.Open("Organizar automáticamente",
                "Los iconos se ordenarán por categoría y se desharán tus grupos. Tus archivos no se mueven.",
                new[] { "Organizar", "Cancelar" }, r => { if (r == "Organizar") App.Instance?.Launcher?.BuildDefault(); });

            UnitC.Checked += (s, e) => Change(x => x.WeatherFahrenheit = false, true);
            UnitF.Checked += (s, e) => Change(x => x.WeatherFahrenheit = true, true);
            CityButton.Click += async (s, e) => await SearchCity();
            CityBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) await SearchCity(); };
            CityClear.Click += (s, e) =>
            {
                Change(x => { x.WeatherQuery = ""; x.WeatherPlace = ""; x.WeatherLat = 0; x.WeatherLon = 0; });
                CityBox.Text = "";
                CityStatus.Text = "Sin ciudad.";
            };

            AutostartSwitch.Click += (s, e) => App.Instance?.SetAutostart(AutostartSwitch.IsChecked == true);
            RestoreButton.Click += (s, e) => App.Instance?.RestoreOriginal();
            ExitButton.Click += (s, e) => App.Instance?.ExitApp();

            var v = Assembly.GetExecutingAssembly().GetName().Version;
            Footer.Text = $"WMac Desktop {v.Major}.{v.Minor}.{v.Build}  ·  Ajustes y registro en {Log.DataDir}";
        }

        public void Refresh()
        {
            _loading = true;
            var s = SettingsStore.Current;
            DesktopSwitch.IsChecked = App.Instance?.Host?.IsEnabled == true;
            SizeSmall.IsChecked = s.IconSize == "small";
            SizeMedium.IsChecked = s.IconSize == "medium";
            SizeLarge.IsChecked = s.IconSize == "large";
            SizeXLarge.IsChecked = s.IconSize == "xlarge";
            IconsSwitch.IsChecked = s.ShowIcons;
            ClockSwitch.IsChecked = s.ShowClock;
            SecondsSwitch.IsChecked = s.ShowSeconds;
            SecondsSwitch.IsEnabled = s.ShowClock;
            DateSwitch.IsChecked = s.ShowDate;
            DateSwitch.IsEnabled = s.ShowClock;
            MusicSwitch.IsChecked = s.ShowMusic;
            CalendarSwitch.IsChecked = s.ShowCalendar;
            WeatherSwitch.IsChecked = s.ShowWeather;
            UnitC.IsChecked = !s.WeatherFahrenheit;
            UnitF.IsChecked = s.WeatherFahrenheit;
            CityBox.Text = s.WeatherQuery;
            CityStatus.Text = s.WeatherConfigured
                ? "Ciudad: " + s.WeatherPlace
                : "Escribe tu ciudad y pulsa Buscar.";
            AutostartSwitch.IsChecked = Autostart.IsEnabled();
            ProtectSwitch.IsChecked = s.ProtectFiles;
            var latest = LayoutBackups.Latest();
            BackupStatus.Text = latest == null
                ? "Aún no hay respaldo. Se guarda uno automáticamente al activar WMac."
                : $"Último respaldo: {latest.When}. El original se conserva siempre.";
            BackupFolderButton.IsEnabled = System.IO.Directory.Exists(LayoutBackups.Dir);
            _loading = false;
        }

        void Bind(CheckBox box, Action<AppSettings, bool> apply)
        {
            box.Checked += (s, e) => Change(x => apply(x, true));
            box.Unchecked += (s, e) => Change(x => apply(x, false));
        }

        void Change(Action<AppSettings> apply, bool refreshWeather = false)
        {
            if (_loading) return;
            apply(SettingsStore.Current);
            SettingsStore.NotifyChanged();
            if (refreshWeather) WeatherService.Shared.Refresh();
            SecondsSwitch.IsEnabled = DateSwitch.IsEnabled = SettingsStore.Current.ShowClock;
        }

        async System.Threading.Tasks.Task SearchCity()
        {
            string q = CityBox.Text.Trim();
            if (q.Length < 2) return;
            CityButton.IsEnabled = false;
            CityStatus.Text = "Buscando…";
            try
            {
                var r = await WeatherService.Geocode(q);
                if (r == null)
                {
                    CityStatus.Text = "No se encontró esa ciudad. Prueba con otro nombre.";
                    return;
                }
                var s = SettingsStore.Current;
                s.WeatherQuery = q;
                s.WeatherPlace = r.Value.Place;
                s.WeatherLat = r.Value.Lat;
                s.WeatherLon = r.Value.Lon;
                s.ShowWeather = true;
                SettingsStore.NotifyChanged();
                WeatherService.Shared.Refresh();
                Refresh();
            }
            catch (Exception ex)
            {
                Log.Write("geocode: " + ex.Message);
                CityStatus.Text = "Sin conexión. Inténtalo más tarde.";
            }
            finally { CityButton.IsEnabled = true; }
        }
    }
}
