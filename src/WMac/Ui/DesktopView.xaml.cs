using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>Contenido que se aloja en una superficie del escritorio o en la vista previa.</summary>
    internal interface ISurfaceContent
    {
        event Action Ready;
        FrameworkElement Element { get; }
        double DeviceScale { get; set; }
        void LoadWallpaper(WallpaperSpec spec, Native.RECT surfacePx, Native.RECT virtualPx, double pxPerDip);
        void SetWorkArea(Thickness insets);
        void PlayIntro();
        void TakeFocus();
        void Dispose();
    }

    /// <summary>
    /// Lanzador: reloj y música a la izquierda, clima y calendario a la derecha,
    /// cuadrícula centrada por páginas, búsqueda instantánea y carpetas virtuales.
    /// </summary>
    internal partial class DesktopView : UserControl, ISurfaceContent
    {
        const double PagePad = 26;   // margen interior para que el zoom y las sombras no se recorten

        sealed class Metrics
        {
            public double W, H, Margin, ColW, Gap, Scale, CellW, CellH, OffX, OffY, PageW;
            public bool ShowColumns;
            public int GridCols, GridRows;
            public Rect Area;
            public TileMetrics Tile;
            public int Capacity => GridCols * GridRows;
        }

        readonly DesktopModel _model;
        readonly Launcher _launcher;
        readonly bool _preview, _effects;
        readonly Dictionary<string, LauncherTile> _tiles = new Dictionary<string, LauncherTile>();
        readonly HashSet<LauncherTile> _selected = new HashSet<LauncherTile>();
        readonly DispatcherTimer _relayoutTimer;
        readonly IComparer<string> _logical = Comparer<string>.Create((a, b) => Native.StrCmpLogicalW(a ?? "", b ?? ""));

        List<LauncherEntry> _display = new List<LauncherEntry>();
        Metrics _m;
        TileMetrics _tm;
        int _page, _pageCount = 1;
        string _query = "";
        Category? _filter;
        bool _ready, _disposed, _introPlayed, _searchFocused;
        int _wallTicket;
        string _myPendingRename;
        BitmapCache _tileCache;
        HwndSource _hookedSource;

        public event Action Ready;
        public FrameworkElement Element => this;
        public double DeviceScale { get; set; }

        internal DesktopView(DesktopModel model, Launcher launcher, bool preview)
        {
            InitializeComponent();
            _model = model;
            _launcher = launcher;
            _preview = preview;
            _effects = (RenderCapability.Tier >> 16) >= 2;

            Marquee.SetResourceReference(Shape.FillProperty, "AccentSoftBrush");
            Marquee.SetResourceReference(Shape.StrokeProperty, "AccentBrush");

            _relayoutTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
            _relayoutTimer.Tick += (s, e) => { _relayoutTimer.Stop(); Relayout(true); };

            _launcher.Changed += OnLauncherChanged;
            _model.RenameRequested += OnRenameRequested;
            SettingsStore.Changed += OnSettingsChanged;
            Stage.SizeChanged += (s, e) => ScheduleRelayout();

            InitInput();
            InitSearch();
            InitFolder();
            Loaded += (s, e) => HookHorizontalWheel();
            Unloaded += (s, e) => UnhookHorizontalWheel();

            Clock.Start();
            Music.Start();
            Weather.Start();
            Calendar.Start();
            SyncTiles();
        }

        // ================= superficie =================

        public void LoadWallpaper(WallpaperSpec spec, Native.RECT surfacePx, Native.RECT virtualPx, double pxPerDip)
        {
            int ticket = ++_wallTicket;
            if (WallpaperHost.Content == null)
            {
                var bg0 = new SolidColorBrush(spec.Background);
                bg0.Freeze();
                Root.Background = bg0;
            }
            var w = Work.Margin;
            var widgetRegion = new Rect((w.Left + 20) * pxPerDip, (w.Top + 20) * pxPerDip, 300 * pxPerDip, 640 * pxPerDip);
            WallpaperRenderer.LoadAsync(spec, surfacePx, virtualPx, widgetRegion, Dispatcher, bm =>
            {
                if (_disposed || ticket != _wallTicket) return;
                var bg = new SolidColorBrush(spec.Background);
                bg.Freeze();
                Root.Background = bg;
                WallpaperHost.Content = WallpaperRenderer.BuildLayer(bm, surfacePx.Width, surfacePx.Height, pxPerDip, soft: true);
                ApplyGlassTheme(bm.WidgetLuma > 0.66);
                GlassCard.SetBackdropHost(Root, Root);
                GlassCard.SetBackdrop(Root, bm.Blur);
                if (!_ready)
                {
                    _ready = true;
                    Ready?.Invoke();
                }
            });
        }

        public void SetWorkArea(Thickness insets) => Work.Margin = insets;

        public void TakeFocus()
        {
            if (!IsKeyboardFocusWithin) Keyboard.Focus(this);
        }

        // ---- animación de presentación: widgets que se deslizan desde su lado e iconos en ola escalonada ----
        internal const double IntroDuration = 1500;
        DateTime _introStart;
        bool _introRunning;

        public void PlayIntro()
        {
            if (_introPlayed) return;
            _introPlayed = true;
            ApplyIntro(0);
            _introStart = DateTime.UtcNow;
            _introRunning = true;
            // Solo se dibuja mientras dura (≈1,5 s); después no queda nada en marcha.
            CompositionTarget.Rendering += OnIntroFrame;
        }

        void OnIntroFrame(object sender, EventArgs e)
        {
            double ms = (DateTime.UtcNow - _introStart).TotalMilliseconds;
            if (_disposed || ms >= IntroDuration)
            {
                ApplyIntro(IntroDuration);
                _introRunning = false;
                CompositionTarget.Rendering -= OnIntroFrame;
                return;
            }
            ApplyIntro(ms);
        }

        static double EaseOutQuart(double t) => 1 - Math.Pow(1 - Clamp(t, 0, 1), 4);

        static double EaseOutBack(double t)
        {
            t = Clamp(t, 0, 1);
            const double c1 = 1.2, c3 = c1 + 1;
            return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
        }

        /// <summary>Estado visual de la presentación en el instante <paramref name="ms"/> (determinista).</summary>
        internal void ApplyIntro(double ms)
        {
            var widgets = new (FrameworkElement el, double dx, double dy, double delay)[]
            {
                (SearchPill, 0, -14, 40), (Clock, -70, 0, 100), (Music, -70, 0, 220), (Weather, 70, 0, 160), (Calendar, 70, 0, 280)
            };
            foreach (var (el, dx, dy, delay) in widgets)
            {
                double k = EaseOutQuart((ms - delay) / 700);
                el.Opacity = Clamp((ms - delay) / 450, 0, 1);
                el.RenderTransform = k >= 1 ? Transform.Identity : new TranslateTransform(dx * (1 - k), dy * (1 - k));
            }
            Dots.Opacity = Clamp((ms - 900) / 400, 0, 1);

            if (_m == null) return;
            int cap = _m.Capacity;
            for (int i = 0; i < _display.Count; i++)
            {
                if (!_tiles.TryGetValue(_display[i].Key, out var t)) continue;
                if (i / cap != _page) { t.SetIntro(1, 1, 0); continue; }
                int local = i % cap, col = local % _m.GridCols, row = local / _m.GridCols;
                double delay = 300 + (row * 0.9 + col) * 55;          // ola diagonal
                double p = (ms - delay) / 520;
                t.SetIntro(Clamp((ms - delay) / 260, 0, 1), 0.78 + 0.22 * EaseOutBack(p), 16 * (1 - EaseOutQuart(p)));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_introRunning) { CompositionTarget.Rendering -= OnIntroFrame; _introRunning = false; }
            _relayoutTimer.Stop();
            UnhookHorizontalWheel();
            _launcher.Changed -= OnLauncherChanged;
            _model.RenameRequested -= OnRenameRequested;
            SettingsStore.Changed -= OnSettingsChanged;
            Clock.Stop();
            Music.Stop();
            Weather.Stop();
            Calendar.Stop();
            foreach (var t in _tiles.Values) t.Detach();
            foreach (var t in _folderTiles.Values) t.Detach();
        }

        void ApplyGlassTheme(bool light)
        {
            var r = Resources;
            if (light)
            {
                r["GlassTint"] = Brush(0x80FFFFFF);
                r["GlassSheen"] = Vertical(0x80FFFFFF, 0x00FFFFFF);
                r["GlassRim"] = Diagonal(0xF2FFFFFF, 0x59FFFFFF, 0x33FFFFFF, 0xB3FFFFFF);
                r["GlassInnerRim"] = Vertical(0xCCFFFFFF, 0x00FFFFFF);
                r["GlassText"] = Brush(0xF0111114);
                r["GlassSubText"] = Brush(0x99111114);
                r["GlassDim"] = Brush(0x66111114);
            }
            else
            {
                r["GlassTint"] = Brush(0x2E0E0E16);
                r["GlassSheen"] = Vertical(0x2BFFFFFF, 0x00FFFFFF);
                r["GlassRim"] = Diagonal(0xA6FFFFFF, 0x2BFFFFFF, 0x14FFFFFF, 0x66FFFFFF);
                r["GlassInnerRim"] = Vertical(0x47FFFFFF, 0x00FFFFFF);
                r["GlassText"] = Brush(0xFFFFFFFF);
                r["GlassSubText"] = Brush(0xBDFFFFFF);
                r["GlassDim"] = Brush(0x5CFFFFFF);
            }
        }

        static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        static Brush Brush(uint argb) { var b = new SolidColorBrush(C(argb)); b.Freeze(); return b; }
        static Brush Vertical(uint a, uint b)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(C(a), 0));
            g.GradientStops.Add(new GradientStop(C(b), 0.5));
            g.Freeze();
            return g;
        }
        static Brush Diagonal(uint a, uint b, uint c, uint d)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(C(a), 0));
            g.GradientStops.Add(new GradientStop(C(b), 0.32));
            g.GradientStops.Add(new GradientStop(C(c), 0.62));
            g.GradientStops.Add(new GradientStop(C(d), 1));
            g.Freeze();
            return g;
        }

        // ================= contenido mostrado =================

        void OnLauncherChanged()
        {
            if (_disposed) return;
            if (_dragging || _folderDragging) return;   // al soltar se vuelve a sincronizar
            SyncTiles();
            RefreshFolder();
        }

        void OnSettingsChanged()
        {
            if (_disposed) return;
            SyncTiles();
        }

        void ScheduleRelayout()
        {
            _relayoutTimer.Stop();
            _relayoutTimer.Start();
        }

        void RebuildDisplay()
        {
            if (_query.Length > 0) _display = Search(_query);
            else if (_filter != null)
                _display = _launcher.AllItems.Where(i => i.Category == _filter.Value).OrderBy(i => i.Name, _logical).Select(i => new LauncherEntry(i)).ToList();
            else if (!SettingsStore.Current.ShowIcons) _display = new List<LauncherEntry>();
            else _display = _launcher.Top.ToList();
        }

        List<LauncherEntry> Search(string query)
        {
            var words = Categorizer.Words(query).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return new List<LauncherEntry>();
            var found = new List<(int rank, LauncherEntry entry)>();
            foreach (var it in _launcher.AllItems)
            {
                string name = " " + Categorizer.Words(it.Name);
                string cat = Categorizer.Words(it.Category.ToString());
                if (words.All(w => name.Contains(w)))
                {
                    int rank = name.StartsWith(" " + words[0]) ? 0 : name.Contains(" " + words[0]) ? 1 : 2;
                    found.Add((rank, new LauncherEntry(it)));
                }
                else if (words.Length == 1 && cat.StartsWith(words[0])) found.Add((3, new LauncherEntry(it)));
            }
            foreach (var e in _launcher.Top.Where(e => e.IsGroup))
            {
                string name = " " + Categorizer.Words(e.Group.Name);
                if (words.All(w => name.Contains(w))) found.Add((1, e));
            }
            return found.OrderBy(f => f.rank).ThenBy(f => f.entry.Name, _logical).Select(f => f.entry).ToList();
        }

        bool CanReorder => _query.Length == 0 && _filter == null;

        void SyncTiles()
        {
            if (_disposed) return;
            RebuildDisplay();
            var tm = _tm ?? new TileMetrics { Icon = 64, CellW = 110, CellH = 120, Font = 12.5, LineH = 17 };
            var needed = new HashSet<string>(_display.Select(e => e.Key));
            foreach (var e in _display)
            {
                if (_tiles.TryGetValue(e.Key, out var t))
                {
                    t.Entry = e;
                    t.Refresh();
                    continue;
                }
                t = e.IsGroup ? (LauncherTile)new FolderTile(e, tm, _effects, _launcher) : new IconTile(e, tm, _effects);
                if (_tileCache != null) t.CacheMode = _tileCache;
                _tiles[e.Key] = t;
                Strip.Children.Add(t);
                if (_introPlayed && CanReorder) t.FadeIn(80);
            }
            foreach (var kv in _tiles.Where(kv => !needed.Contains(kv.Key)).ToList())
            {
                Strip.Children.Remove(kv.Value);
                kv.Value.Detach();
                _selected.Remove(kv.Value);
                _tiles.Remove(kv.Key);
            }
            Relayout(true);
        }

        // ================= composición =================

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : v > hi ? hi : v;
        static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;

        Metrics ComputeMetrics(double W, double H)
        {
            var s = SettingsStore.Current;
            var m = new Metrics { W = W, H = H };
            m.Margin = Clamp(W * 0.026, 18, 52);
            bool left = s.ShowClock || s.ShowMusic, right = s.ShowWeather || s.ShowCalendar;
            m.ShowColumns = (left || right) && W >= 980;
            m.ColW = Clamp(W * 0.19, 220, 330);
            m.Gap = Clamp(W * 0.024, 18, 46);
            double side = m.ShowColumns ? m.ColW + m.Gap : 0;
            double gl = m.Margin + side, gr = W - m.Margin - side;
            // Bajo la búsqueda queda siempre hueco para las categorías: al aparecer no tapan ni desplazan iconos.
            double top = m.Margin + 36 + 20 + 34, bottom = H - m.Margin - 24;
            m.Area = new Rect(gl, top, Math.Max(120, gr - gl), Math.Max(120, bottom - top));

            double mul = s.IconSize == "small" ? 0.86 : s.IconSize == "large" ? 1.16 : s.IconSize == "xlarge" ? 1.4 : 1.0;
            double icon = Math.Round(Clamp(Math.Min(H / 12.0, m.Area.Width / 9.0), 44, 96) * mul);
            double font = Clamp(Math.Round(icon * 0.37) / 2, 11, 14);
            double lineH = Math.Ceiling(font * 1.36);
            double baseW = icon * 1.62, baseH = 6 + icon + Math.Round(icon * 0.12) + lineH + 16;
            int maxCols = s.IconSize == "small" ? 8 : s.IconSize == "large" ? 6 : s.IconSize == "xlarge" ? 4 : 7;
            int maxRows = s.IconSize == "small" ? 6 : s.IconSize == "large" ? 4 : s.IconSize == "xlarge" ? 3 : 5;
            m.GridCols = Clamp((int)(m.Area.Width / baseW), 3, maxCols);
            m.GridRows = Clamp((int)(m.Area.Height / baseH), 2, maxRows);
            m.CellW = Math.Floor(Math.Min(m.Area.Width / m.GridCols, baseW * 1.5));
            m.CellH = Math.Floor(Math.Min(m.Area.Height / m.GridRows, baseH * 1.4));
            m.OffX = Math.Round((m.Area.Width - m.GridCols * m.CellW) / 2);
            m.OffY = Math.Round((m.Area.Height - m.GridRows * m.CellH) / 2);
            m.PageW = m.Area.Width + 2 * PagePad;
            m.Tile = new TileMetrics { Icon = icon, CellW = m.CellW, CellH = m.CellH, Font = font, LineH = lineH };

            double leftH = (s.ShowClock ? 300 : 0) + (s.ShowClock && s.ShowMusic ? 24 : 0) + (s.ShowMusic ? 344 : 0);
            double rightH = (s.ShowWeather ? 330 : 0) + (s.ShowWeather && s.ShowCalendar ? 24 : 0) + (s.ShowCalendar ? 248 : 0);
            double avail = H - 2 * m.Margin;
            m.Scale = Clamp(Math.Min(m.ColW / 300, avail / Math.Max(1, Math.Max(leftH, rightH))), 0.5, 1.3);
            return m;
        }

        void Relayout(bool animate)
        {
            if (_disposed) return;
            double W = Stage.ActualWidth, H = Stage.ActualHeight;
            if (W < 80 || H < 80) return;
            EnsureTileCache();
            var m = ComputeMetrics(W, H);
            bool metricsChanged = _tm == null || _tm.Icon != m.Tile.Icon || _tm.CellW != m.Tile.CellW || _tm.CellH != m.Tile.CellH;
            _m = m;
            _tm = m.Tile;
            if (metricsChanged)
            {
                foreach (var t in _tiles.Values) t.ApplyMetrics(_tm);
                UpdateIconPx();
                animate = false;
            }

            LayoutChrome(m);

            Canvas.SetLeft(PagesHost, m.Area.X - PagePad);
            Canvas.SetTop(PagesHost, m.Area.Y - PagePad);
            PagesHost.Width = m.Area.Width + 2 * PagePad;
            PagesHost.Height = m.Area.Height + 2 * PagePad;

            _pageCount = Math.Max(1, (int)Math.Ceiling(_display.Count / (double)m.Capacity));
            _page = Clamp(_page, 0, _pageCount - 1);
            var shown = new HashSet<LauncherTile>();
            for (int i = 0; i < _display.Count; i++)
            {
                if (!_tiles.TryGetValue(_display[i].Key, out var t) || t.Parent != Strip) continue;
                PlaceTile(t, SlotPoint(i), animate);
                t.Visibility = Visibility.Visible;
                shown.Add(t);
            }
            foreach (var t in _tiles.Values) if (!shown.Contains(t) && t.Parent == Strip) t.Visibility = Visibility.Collapsed;

            StripShift.BeginAnimation(TranslateTransform.XProperty, null);
            StripShift.X = -_page * m.PageW;
            UpdateDots();
            UpdateEmpty();
            if (_openGroup != null) LayoutFolder();
        }

        Point SlotPoint(int index)
        {
            int cap = _m.Capacity;
            int page = index / cap, idx = index % cap;
            return new Point(page * _m.PageW + PagePad + _m.OffX + (idx % _m.GridCols) * _m.CellW,
                             PagePad + _m.OffY + (idx / _m.GridCols) * _m.CellH);
        }

        /// <summary>Convierte coordenadas de la tira de páginas a coordenadas del escenario (página actual).</summary>
        Point StripToStage(Point p) => new Point(p.X - _page * _m.PageW + _m.Area.X - PagePad, p.Y + _m.Area.Y - PagePad);
        Point StageToStrip(Point p) => new Point(p.X + _page * _m.PageW - _m.Area.X + PagePad, p.Y - _m.Area.Y + PagePad);

        static void PlaceTile(LauncherTile t, Point p, bool animate, double pageW = double.MaxValue)
        {
            double ox = Canvas.GetLeft(t), oy = Canvas.GetTop(t);
            Canvas.SetLeft(t, p.X);
            Canvas.SetTop(t, p.Y);
            if (!animate || double.IsNaN(ox) || double.IsNaN(oy)) return;
            double dx = ox - p.X, dy = oy - p.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) return;
            if (Math.Abs(dx) > pageW * 0.75) return;
            t.SlideFrom(dx, dy);
        }

        void LayoutChrome(Metrics m)
        {
            var s = SettingsStore.Current;
            double k = m.Scale, cw = 300 * k;
            void Show(FrameworkElement el, bool on, double x, double y)
            {
                el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (!on) return;
                if (!(el.LayoutTransform is ScaleTransform st) || st.ScaleX != k) el.LayoutTransform = new ScaleTransform(k, k);
                Canvas.SetLeft(el, Math.Round(x));
                Canvas.SetTop(el, Math.Round(y));
            }

            bool cols = m.ShowColumns;
            Show(Clock, cols && s.ShowClock, m.Margin, m.Margin);
            Show(Music, cols && s.ShowMusic, m.Margin, m.Margin + (s.ShowClock ? (300 + 24) * k : 0));
            double rx = m.W - m.Margin - cw;
            Show(Weather, cols && s.ShowWeather, rx, m.Margin);
            double calTop = s.ShowWeather ? Math.Max(m.H - m.Margin - 248 * k, m.Margin + (330 + 24) * k) : m.Margin;
            Show(Calendar, cols && s.ShowCalendar, rx, calTop);

            double pillW = _searchFocused || _query.Length > 0 ? 440 : 300;
            AnimateSearchPill(pillW, (m.W - pillW) / 2, m.Margin);

            Chips.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(Chips, Math.Round((m.W - Chips.DesiredSize.Width) / 2));
            Canvas.SetTop(Chips, m.Margin + 46);

            Dots.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(Dots, Math.Round((m.W - Dots.DesiredSize.Width) / 2));
            Canvas.SetTop(Dots, m.H - m.Margin - 16);
        }

        double _pillTargetW = -1;

        void AnimateSearchPill(double width, double left, double top)
        {
            Canvas.SetTop(SearchPill, top);
            if (_pillTargetW < 0)
            {
                SearchPill.Width = width;
                Canvas.SetLeft(SearchPill, left);
                _pillTargetW = width;
                return;
            }
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(_pillTargetW == width ? 0 : 220);
            _pillTargetW = width;
            SearchPill.BeginAnimation(WidthProperty, new DoubleAnimation(width, d) { EasingFunction = ease });
            SearchPill.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(left, d) { EasingFunction = ease });
        }

        void UpdateIconPx()
        {
            if (_tm == null) return;
            double scale = DeviceScale > 0 ? DeviceScale : VisualTreeHelper.GetDpi(this).DpiScaleX;
            _model.SetIconPx((int)Math.Ceiling(_tm.Icon * 0.8 * scale * 1.05));
        }

        // ================= rendimiento =================

        /// <summary>
        /// Cada icono (placa de cristal, sombra y etiqueta) se dibuja una vez en una textura de la GPU. Al pasar de página
        /// solo se desplazan texturas: las sombras no se vuelven a calcular en cada fotograma.
        /// </summary>
        void EnsureTileCache()
        {
            double src = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double dev = DeviceScale > 0 ? DeviceScale : src;
            double scale = Math.Max(1, dev / Math.Max(0.5, src));   // nítido aunque el monitor tenga otra escala que la ventana
            if (_tileCache != null && Math.Abs(_tileCache.RenderAtScale - scale) < 0.01) return;
            _tileCache = new BitmapCache { RenderAtScale = scale, SnapsToDevicePixels = true };
            _tileCache.Freeze();
            foreach (var t in _tiles.Values) t.CacheMode = _tileCache;
            foreach (var t in _folderTiles.Values) t.CacheMode = _tileCache;
        }

        static readonly IEasingFunction PageEase = Frozen(new QuarticEase { EasingMode = EasingMode.EaseOut });
        static IEasingFunction Frozen(QuarticEase e) { e.Freeze(); return e; }

        /// <summary>Desliza una tira de páginas. Si ya se movía, sigue desde donde está (sin saltos al encadenar páginas).</summary>
        internal static void SlideStrip(TranslateTransform shift, double to) =>
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(380)) { EasingFunction = PageEase },
                                 HandoffBehavior.SnapshotAndReplace);

        // ---- rueda y panel táctil ----
        DateTime _wheelLast = DateTime.MinValue;
        bool _gestureUsed;

        /// <summary>
        /// Rueda con muescas: una página por muesca, al instante (las muescas rápidas encadenan páginas).
        /// Panel táctil o rueda suave: un gesto = una página; la inercia de ese mismo gesto no pasa más.
        /// Devuelve +1 (siguiente), -1 (anterior) o 0.
        /// </summary>
        int WheelStep(int delta)
        {
            var now = DateTime.UtcNow;
            double idle = (now - _wheelLast).TotalMilliseconds;
            _wheelLast = now;
            if (delta == 0) return 0;
            if (delta % 120 == 0)
            {
                if ((now - _lastWheelFlip).TotalMilliseconds < 130) return 0;
                _lastWheelFlip = now;
                _wheelAcc = 0;
                return delta < 0 ? 1 : -1;
            }
            if (idle > 200) { _wheelAcc = 0; _gestureUsed = false; }
            if (_gestureUsed) return 0;
            _wheelAcc += delta;
            if (Math.Abs(_wheelAcc) < 50) return 0;
            _gestureUsed = true;
            _lastWheelFlip = now;
            return _wheelAcc < 0 ? 1 : -1;
        }

        void PageByWheel(int delta)
        {
            int dir = WheelStep(delta);
            if (dir == 0) return;
            if (_openGroup != null) FolderGoToPage(_fPage + dir);
            else GoToPage(_page + dir);
        }

        // Desplazamiento horizontal (panel táctil con dos dedos o rueda inclinable): WPF no lo expone, se lee del mensaje.
        void HookHorizontalWheel()
        {
            if (_hookedSource != null) return;
            _hookedSource = PresentationSource.FromVisual(this) as HwndSource;
            _hookedSource?.AddHook(OnWindowMessage);
        }

        void UnhookHorizontalWheel()
        {
            _hookedSource?.RemoveHook(OnWindowMessage);
            _hookedSource = null;
        }

        IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WM_MOUSEHWHEEL = 0x020E;
            if (msg != WM_MOUSEHWHEEL || _disposed || _dragging || _folderDragging) return IntPtr.Zero;
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            PageByWheel(-delta);   // hacia la derecha = página siguiente
            handled = true;
            return IntPtr.Zero;
        }

        // ================= páginas =================

        void UpdateDots()
        {
            if (Dots.Children.Count != _pageCount)
            {
                Dots.Children.Clear();
                for (int i = 0; i < _pageCount; i++)
                {
                    int page = i;
                    var b = new Button { Style = (Style)FindResource("PageDot"), ToolTip = $"Página {i + 1}" };
                    b.Click += (s, e) => GoToPage(page);
                    Dots.Children.Add(b);
                }
                if (_m != null)
                {
                    Dots.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    Canvas.SetLeft(Dots, Math.Round((_m.W - Dots.DesiredSize.Width) / 2));
                }
            }
            Dots.Visibility = _pageCount > 1 ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < Dots.Children.Count; i++)
                ((Button)Dots.Children[i]).Opacity = i == _page ? 0.95 : 0.35;
        }

        void GoToPage(int page)
        {
            page = Clamp(page, 0, _pageCount - 1);
            if (page == _page || _m == null) return;
            _page = page;
            SlideStrip(StripShift, -_page * _m.PageW);
            UpdateDots();
        }

        void UpdateEmpty()
        {
            bool empty = _display.Count == 0 && (_query.Length > 0 || _filter != null);
            EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (!empty || _m == null) return;
            EmptyText.Text = _query.Length > 0 ? $"Sin resultados para «{_query}»" : "No hay elementos en esta categoría";
            EmptyText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(EmptyText, Math.Round(_m.Area.X + (_m.Area.Width - EmptyText.DesiredSize.Width) / 2));
            Canvas.SetTop(EmptyText, Math.Round(_m.Area.Y + _m.Area.Height * 0.35));
        }

        // ================= búsqueda y categorías =================

        void InitSearch()
        {
            SearchBox.TextChanged += (s, e) => SetQuery(SearchBox.Text);
            SearchBox.GotKeyboardFocus += (s, e) => SetSearchFocused(true);
            SearchBox.LostKeyboardFocus += (s, e) => SetSearchFocused(false);
            SearchPill.MouseLeftButtonDown += (s, e) => { SearchBox.Focus(); e.Handled = true; };
            SearchClear.Click += (s, e) => ClearSearch(true);
            BuildChips();
        }

        void BuildChips()
        {
            Chips.Children.Clear();
            // Focusable = false: pulsar una categoría no quita el foco al cuadro de búsqueda.
            var all = new RadioButton { Content = "Todo", Style = (Style)FindResource("Chip"), GroupName = "cat", IsChecked = _filter == null, Focusable = false };
            all.Checked += (s, e) => SetFilter(null);
            Chips.Children.Add(all);
            foreach (var c in Categorizer.Order)
            {
                var cat = c;
                var rb = new RadioButton { Content = c.ToString(), Style = (Style)FindResource("Chip"), GroupName = "cat", IsChecked = _filter == c, Tag = c, Focusable = false };
                rb.Checked += (s, e) => SetFilter(cat);
                Chips.Children.Add(rb);
            }
        }

        void UpdateChipsVisibility()
        {
            foreach (var child in Chips.Children.OfType<RadioButton>())
                if (child.Tag is Category c) child.Visibility = _launcher.AllItems.Any(i => i.Category == c) ? Visibility.Visible : Visibility.Collapsed;
            bool show = _searchFocused || _filter != null;
            if (show == (Chips.Visibility == Visibility.Visible && Chips.Opacity > 0.5)) return;
            if (show)
            {
                Chips.Visibility = Visibility.Visible;
                Chips.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            }
            else
            {
                var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
                a.Completed += (s, e) => { if (!(_searchFocused || _filter != null)) Chips.Visibility = Visibility.Collapsed; };
                Chips.BeginAnimation(OpacityProperty, a);
            }
        }

        void SetSearchFocused(bool focused)
        {
            // Al pulsar una categoría el cuadro pierde el foco un instante: se espera antes de ocultarlas.
            if (!focused && Chips.IsMouseOver) return;
            _searchFocused = focused;
            UpdateChipsVisibility();
            if (_m != null) LayoutChrome(_m);
        }

        void SetQuery(string text)
        {
            text = (text ?? "").Trim();
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SearchClear.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text == _query) return;
            _query = text;
            _page = 0;
            ClearSelection();
            SyncTiles();
            if (_query.Length > 0) foreach (var t in _tiles.Values) if (t.Visibility == Visibility.Visible) t.FadeIn(0);
        }

        void SetFilter(Category? c)
        {
            if (_filter == c) return;
            _filter = c;
            _page = 0;
            ClearSelection();
            UpdateChipsVisibility();
            SyncTiles();
            foreach (var t in _tiles.Values) if (t.Visibility == Visibility.Visible) t.FadeIn(0);
        }

        void ClearSearch(bool keepFocus)
        {
            SearchBox.Text = "";
            if (_filter != null)
            {
                _filter = null;
                foreach (var rb in Chips.Children.OfType<RadioButton>()) rb.IsChecked = rb.Tag == null;
                SyncTiles();
            }
            if (keepFocus) SearchBox.Focus();
            else Keyboard.Focus(this);
            UpdateChipsVisibility();
        }
    }
}
