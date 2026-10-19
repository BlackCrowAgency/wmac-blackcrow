using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>
    /// Una ventana hija por monitor, colocada dentro de la vista del escritorio de Windows
    /// (SHELLDLL_DefView) por encima de la lista de iconos nativa. No oculta ni modifica nada de
    /// Explorer: solo la cubre. Si WMac se cierra o falla, Windows destruye esta ventana y el
    /// escritorio original vuelve a verse al instante.
    /// </summary>
    internal sealed class DesktopSurface : IDisposable
    {
        public readonly MonitorInfo Monitor;
        public readonly bool IsPrimary;
        public event Action Shown;

        readonly HwndSource _source;
        readonly ISurfaceContent _content;
        readonly DispatcherTimer _readyTimeout;
        DispatcherTimer _fade;
        Native.RECT _rect;
        int _alpha;
        bool _shown, _disposed;

        public IntPtr Hwnd => _disposed || _source == null || _source.IsDisposed ? IntPtr.Zero : _source.Handle;
        public bool IsShown => _shown && !_disposed;

        public DesktopSurface(IntPtr parent, MonitorInfo monitor, bool primary, DesktopModel model, Launcher launcher, WallpaperSpec spec, Native.RECT virtualBounds)
        {
            Monitor = monitor;
            IsPrimary = primary;

            var r = monitor.Bounds;
            Native.MapWindowPoints(IntPtr.Zero, parent, ref r, 2);   // pantalla → coordenadas cliente del padre
            _rect = r;

            var p = new HwndSourceParameters("WMacDesktopSurface")
            {
                ParentWindow = parent,
                WindowStyle = Native.WS_CHILD | Native.WS_CLIPSIBLINGS | Native.WS_CLIPCHILDREN,
                // NOPARENTNOTIFY: los clics no se notifican de forma síncrona al hilo del Explorador.
                ExtendedWindowStyle = Native.WS_EX_LAYERED | Native.WS_EX_NOPARENTNOTIFY,
                PositionX = r.Left,
                PositionY = r.Top,
                Width = r.Width,
                Height = r.Height
            };
            _source = new HwndSource(p);
            // Transparente hasta que el contenido esté listo: nunca se tapa el escritorio con algo a medio dibujar.
            Native.SetLayeredWindowAttributes(_source.Handle, 0, 0, Native.LWA_ALPHA);
            _source.AddHook(Hook);

            double srcScale = _source.CompositionTarget.TransformToDevice.M11;
            double monScale = monitor.Scale;
            _content = primary ? (ISurfaceContent)new DesktopView(model, launcher, false) : new WallpaperView();
            _content.DeviceScale = monScale;
            var el = _content.Element;
            el.Width = r.Width / monScale;
            el.Height = r.Height / monScale;
            if (Math.Abs(monScale / srcScale - 1) > 0.01)
                el.LayoutTransform = new ScaleTransform(monScale / srcScale, monScale / srcScale);

            var w = monitor.Work;
            var b = monitor.Bounds;
            _content.SetWorkArea(new Thickness((w.Left - b.Left) / monScale, (w.Top - b.Top) / monScale,
                                               (b.Right - w.Right) / monScale, (b.Bottom - w.Bottom) / monScale));
            _source.RootVisual = el;

            _readyTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _readyTimeout.Tick += (s, e) => OnReady();
            _content.Ready += OnReady;
            _content.LoadWallpaper(spec, monitor.Bounds, virtualBounds, monScale);

            Native.SetWindowPos(_source.Handle, Native.HWND_TOP, r.Left, r.Top, r.Width, r.Height,
                                Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
            _readyTimeout.Start();
        }

        void OnReady()
        {
            _readyTimeout.Stop();
            if (_shown || _disposed) return;
            _shown = true;
            // Esperar a que se haya dibujado el primer fotograma antes de hacerse visible.
            _source.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, (Action)(() =>
            {
                if (_disposed) return;
                EnsureTop();
                FadeTo(255, 240, null);
                _content.PlayIntro();
                Shown?.Invoke();
            }));
        }

        void FadeTo(int target, int ms, Action done)
        {
            _fade?.Stop();
            int start = _alpha;
            var began = DateTime.UtcNow;
            _fade = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            _fade.Tick += (s, e) =>
            {
                double t = Math.Min(1, (DateTime.UtcNow - began).TotalMilliseconds / ms);
                double eased = 1 - Math.Pow(1 - t, 3);
                SetAlpha((int)Math.Round(start + (target - start) * eased));
                if (t >= 1)
                {
                    _fade.Stop();
                    done?.Invoke();
                }
            };
            _fade.Start();
        }

        void SetAlpha(int a)
        {
            _alpha = Math.Max(0, Math.Min(255, a));
            if (Hwnd != IntPtr.Zero) Native.SetLayeredWindowAttributes(Hwnd, 0, (byte)_alpha, Native.LWA_ALPHA);
        }

        public void FadeOut(Action done)
        {
            if (_disposed || !_shown || Hwnd == IntPtr.Zero) { done?.Invoke(); return; }
            FadeTo(0, 180, done);
        }

        public void EnsureTop()
        {
            if (Hwnd != IntPtr.Zero)
                Native.SetWindowPos(Hwnd, Native.HWND_TOP, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        public void TakeFocus()
        {
            if (!IsShown || Hwnd == IntPtr.Zero) return;
            Native.SetFocus(Hwnd);
            _content.TakeFocus();
        }

        public void UpdateWallpaper(WallpaperSpec spec, Native.RECT virtualBounds)
        {
            if (_disposed) return;
            _content.LoadWallpaper(spec, Monitor.Bounds, virtualBounds, Monitor.Scale);
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_CONTEXTMENU)
            {
                // Una ventana hija reenvía WM_CONTEXTMENU a su padre (la vista del Explorador), que abriría
                // su menú nativo para los iconos ocultos. WMac muestra sus propios menús: se consume aquí.
                handled = true;
                return IntPtr.Zero;
            }
            if (msg == Native.WM_SETFOCUS)
                _source.Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => { if (!_disposed) _content.TakeFocus(); }));
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _readyTimeout.Stop();
            _fade?.Stop();
            try { _content.Dispose(); } catch (Exception ex) { Log.Error("surface content", ex); }
            try
            {
                if (!_source.IsDisposed)
                {
                    _source.RemoveHook(Hook);
                    _source.RootVisual = null;
                    _source.Dispose();   // destruye la ventana hija
                }
            }
            catch (Exception ex) { Log.Error("surface dispose", ex); }
        }
    }
}
