using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>Vista previa en una ventana normal: permite probar WMac sin integrarlo en el escritorio.</summary>
    internal sealed class PreviewWindow : Window
    {
        readonly DesktopView _view;
        readonly DispatcherTimer _resize;

        public PreviewWindow(DesktopModel model, Launcher launcher)
        {
            Title = "WMac — Vista previa";
            var wa = SystemParameters.WorkArea;
            Width = Math.Round(wa.Width * 0.8);
            Height = Math.Round(wa.Height * 0.8);
            MinWidth = 760;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brushes.Black;
            try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/WMac;component/Assets/WMac.ico")); } catch { }

            _view = new DesktopView(model, launcher, true);
            Content = _view;
            _view.Ready += () => _view.PlayIntro();

            _resize = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _resize.Tick += (s, e) => { _resize.Stop(); LoadWallpaper(); };
            SizeChanged += (s, e) => { _resize.Stop(); _resize.Start(); };
            Closed += (s, e) => { _resize.Stop(); _view.Dispose(); };
        }

        void LoadWallpaper()
        {
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _view.DeviceScale = scale;
            int w = (int)Math.Round(_view.ActualWidth * scale), h = (int)Math.Round(_view.ActualHeight * scale);
            if (w <= 0 || h <= 0) return;
            var spec = WallpaperInfo.Query(Monitors.GetAll()).FirstOrDefault() ?? new WallpaperSpec();
            var rect = new Native.RECT(0, 0, w, h);
            _view.LoadWallpaper(spec, rect, rect, scale);
        }
    }
}
