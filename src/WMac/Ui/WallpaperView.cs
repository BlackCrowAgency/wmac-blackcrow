using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace WMac
{
    /// <summary>Superficie de monitores secundarios: solo el fondo actual (los iconos viven en el principal).</summary>
    internal sealed class WallpaperView : Grid, ISurfaceContent
    {
        readonly ContentControl _host = new ContentControl { IsHitTestVisible = false, Focusable = false };
        int _ticket;
        bool _ready, _disposed;

        public event Action Ready;
        public FrameworkElement Element => this;
        public double DeviceScale { get; set; }

        public WallpaperView()
        {
            Background = Brushes.Black;
            Children.Add(_host);
            MouseRightButtonUp += OnRightUp;
        }

        public void LoadWallpaper(WallpaperSpec spec, Native.RECT surfacePx, Native.RECT virtualPx, double pxPerDip)
        {
            int ticket = ++_ticket;
            WallpaperRenderer.LoadAsync(spec, surfacePx, virtualPx, new Rect(0, 0, 1, 1), Dispatcher, bm =>
            {
                if (_disposed || ticket != _ticket) return;
                var bg = new SolidColorBrush(spec.Background);
                bg.Freeze();
                Background = bg;
                _host.Content = WallpaperRenderer.BuildLayer(bm, surfacePx.Width, surfacePx.Height, pxPerDip);
                if (!_ready) { _ready = true; Ready?.Invoke(); }
            });
        }

        public void SetWorkArea(Thickness insets) { }
        public void PlayIntro() { }
        public void TakeFocus() { }
        public void Dispose() => _disposed = true;

        void OnRightUp(object sender, MouseButtonEventArgs e)
        {
            var cm = new ContextMenu { PlacementTarget = this, Placement = PlacementMode.MousePoint };
            cm.Items.Add(Item("Fondo de pantalla…", () => ShellOps.OpenUri("ms-settings:personalization-background")));
            cm.Items.Add(Item("Configuración de pantalla", () => ShellOps.OpenUri("ms-settings:display")));
            cm.Items.Add(new Separator());
            cm.Items.Add(Item("Ajustes de WMac…", () => App.Instance?.ShowSettings()));
            cm.Items.Add(Item("Mostrar escritorio original", () => App.Instance?.DeactivateDesktop()));
            cm.IsOpen = true;
            e.Handled = true;
        }

        static MenuItem Item(string header, Action click)
        {
            var mi = new MenuItem { Header = header };
            mi.Click += (s, e) => click();
            return mi;
        }
    }
}
