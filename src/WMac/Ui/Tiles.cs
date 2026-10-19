using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace WMac
{
    internal sealed class TileMetrics
    {
        public double Icon, CellW, CellH, Font, LineH;
        public double PlateTop => 6;
        public Point PlateCenter => new Point(CellW / 2, PlateTop + Icon / 2);
    }

    /// <summary>Elemento de la cuadrícula: placa de cristal + etiqueta. Base de iconos y carpetas.</summary>
    internal abstract class LauncherTile : Grid
    {
        protected static readonly Brush PlateFill = Gradient(0x45FFFFFF, 0x14FFFFFF);
        protected static readonly Brush FolderFill = Gradient(0x38FFFFFF, 0x1FFFFFFF);
        static readonly Brush PlateRim = Rim();
        static readonly Effect PlateShadow = FrozenFx(new DropShadowEffect { BlurRadius = 18, ShadowDepth = 6, Direction = 270, Opacity = 0.30, RenderingBias = RenderingBias.Performance });
        static readonly Effect TextShadow = FrozenFx(new DropShadowEffect { BlurRadius = 5, ShadowDepth = 1, Direction = 270, Opacity = 0.85, RenderingBias = RenderingBias.Performance });

        static Brush Gradient(uint top, uint bottom)
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
            g.GradientStops.Add(new GradientStop(C(top), 0));
            g.GradientStops.Add(new GradientStop(C(bottom), 1));
            g.Freeze();
            return g;
        }

        static Brush Rim()
        {
            var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            g.GradientStops.Add(new GradientStop(C(0xB3FFFFFF), 0));
            g.GradientStops.Add(new GradientStop(C(0x26FFFFFF), 0.4));
            g.GradientStops.Add(new GradientStop(C(0x14FFFFFF), 0.7));
            g.GradientStops.Add(new GradientStop(C(0x66FFFFFF), 1));
            g.Freeze();
            return g;
        }

        static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        static Effect FrozenFx(Effect e) { e.Freeze(); return e; }

        public LauncherEntry Entry { get; set; }
        public string Key => Entry.Key;
        public readonly TranslateTransform Offset = new TranslateTransform();
        public bool HoverEnabled = true;
        public TileMetrics Metrics { get; private set; }

        protected readonly Border Plate;
        protected readonly Grid PlateContent = new Grid();
        readonly TextBlock _label;
        readonly Border _pill;
        readonly StackPanel _stack;
        readonly ScaleTransform _scale = new ScaleTransform(1, 1);
        readonly ScaleTransform _introScale = new ScaleTransform(1, 1);
        readonly TranslateTransform _introShift = new TranslateTransform();
        bool _selected, _merge, _hover;

        /// <summary>Estado de la animación de presentación (opacidad, escala y desplazamiento vertical).</summary>
        public void SetIntro(double opacity, double scale, double dy)
        {
            Opacity = opacity;
            _introScale.ScaleX = _introScale.ScaleY = scale;
            _introShift.Y = dy;
        }

        protected LauncherTile(LauncherEntry entry, TileMetrics m, bool effects)
        {
            Entry = entry;
            Background = Brushes.Transparent;
            var transforms = new TransformGroup();
            transforms.Children.Add(_introScale);
            transforms.Children.Add(_introShift);
            transforms.Children.Add(Offset);
            RenderTransform = transforms;
            RenderTransformOrigin = new Point(0.5, 0.42);
            SnapsToDevicePixels = true;

            Plate = new Border
            {
                Background = PlateFill,
                BorderBrush = PlateRim,
                BorderThickness = new Thickness(1),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _scale,
                HorizontalAlignment = HorizontalAlignment.Center,
                Child = PlateContent
            };
            if (effects) Plate.Effect = PlateShadow;

            _label = new TextBlock
            {
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                TextAlignment = TextAlignment.Center,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Medium,
                Text = entry.Name
            };
            TextOptions.SetTextFormattingMode(_label, TextFormattingMode.Display);
            if (effects) _label.Effect = TextShadow;
            _pill = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2), HorizontalAlignment = HorizontalAlignment.Center, Child = _label };

            _stack = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
            _stack.Children.Add(Plate);
            _stack.Children.Add(_pill);
            Children.Add(_stack);

            ToolTip = entry.Name;
            ToolTipService.SetInitialShowDelay(this, 900);
            ToolTipService.SetPlacement(this, PlacementMode.Bottom);
            MouseEnter += (s, e) => SetHover(true);
            MouseLeave += (s, e) => SetHover(false);
            ApplyMetrics(m);
        }

        public virtual void ApplyMetrics(TileMetrics m)
        {
            Metrics = m;
            Width = m.CellW;
            Height = m.CellH;
            Plate.Width = Plate.Height = m.Icon;
            Plate.CornerRadius = new CornerRadius(Math.Round(m.Icon * 0.26));
            _stack.Margin = new Thickness(0, m.PlateTop, 0, 0);
            _pill.Margin = new Thickness(0, Math.Round(m.Icon * 0.12), 0, 0);
            _label.FontSize = m.Font;
            _label.MaxWidth = m.CellW - 16;
        }

        protected void SetLabel(string text)
        {
            _label.Text = text;
            ToolTip = text;
        }

        public bool IsSelected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                UpdateRim();
                if (value) _pill.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
                else _pill.ClearValue(Border.BackgroundProperty);
            }
        }

        void UpdateRim()
        {
            if (_selected || _merge)
            {
                Plate.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
                Plate.BorderThickness = new Thickness(2);
            }
            else
            {
                Plate.BorderBrush = PlateRim;
                Plate.BorderThickness = new Thickness(1);
            }
        }

        /// <summary>Resalta el destino al arrastrar otro icono encima (crear o ampliar un grupo).</summary>
        public void SetMerge(bool on)
        {
            if (_merge == on) return;
            _merge = on;
            UpdateRim();
            ScaleTo(on ? 1.16 : (_hover && HoverEnabled ? 1.06 : 1), 160);
        }

        void SetHover(bool on)
        {
            _hover = on;
            if (_merge) return;
            ScaleTo(on && HoverEnabled ? 1.06 : 1, on ? 140 : 220);
        }

        void ScaleTo(double s, int ms)
        {
            var a = new DoubleAnimation(s, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        }

        public void Press(bool down) => ScaleTo(down ? 0.92 : (_hover ? 1.06 : 1), down ? 90 : 180);

        public void Lift(bool on)
        {
            ScaleTo(on ? 1.12 : 1, 150);
            Opacity = on ? 0.94 : 1;
        }

        public void Bounce()
        {
            var k = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(400) };
            k.KeyFrames.Add(new EasingDoubleKeyFrame(0.84, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.08, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(250)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            k.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(400)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, k);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, k);
        }

        /// <summary>Animación FLIP: ya está en su sitio nuevo y se desliza desde donde estaba.</summary>
        public void SlideFrom(double dx, double dy, int ms = 320)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Offset.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dx, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
            Offset.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dy, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
        }

        public void StopMotion()
        {
            Offset.BeginAnimation(TranslateTransform.XProperty, null);
            Offset.BeginAnimation(TranslateTransform.YProperty, null);
            Offset.X = Offset.Y = 0;
        }

        public void FadeIn(int delayMs)
        {
            var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(delayMs + 260))));
            BeginAnimation(OpacityProperty, fade);
        }

        public virtual void Refresh() => SetLabel(Entry.Name);
        public abstract void Detach();
    }

    internal sealed class IconTile : LauncherTile
    {
        readonly Image _img;
        DesktopItem _item;

        public IconTile(LauncherEntry entry, TileMetrics m, bool effects) : base(entry, m, effects)
        {
            _img = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);
            PlateContent.Children.Add(_img);
            Bind(entry.Item);
            ApplyMetrics(m);
        }

        public DesktopItem Item => _item;

        void Bind(DesktopItem item)
        {
            if (_item == item) return;
            if (_item != null) _item.PropertyChanged -= OnItemChanged;
            _item = item;
            _item.PropertyChanged += OnItemChanged;
            _img.Source = item.Icon;
            SetLabel(item.Name);
        }

        public override void ApplyMetrics(TileMetrics m)
        {
            base.ApplyMetrics(m);
            if (_img == null) return;
            double k = _item != null && _item.AllowThumbnail ? 0.8 : 0.66;
            _img.Width = _img.Height = Math.Round(m.Icon * k);
        }

        public override void Refresh()
        {
            Bind(Entry.Item);
            SetLabel(Entry.Item.Name);
        }

        void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DesktopItem.Icon))
            {
                bool first = _img.Source == null;
                _img.Source = _item.Icon;
                if (first) _img.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
            }
            else if (e.PropertyName == nameof(DesktopItem.Name)) SetLabel(_item.Name);
        }

        public override void Detach()
        {
            if (_item != null) _item.PropertyChanged -= OnItemChanged;
        }
    }

    /// <summary>Carpeta virtual estilo macOS: placa de cristal con miniaturas de su contenido.</summary>
    internal sealed class FolderTile : LauncherTile
    {
        readonly Launcher _launcher;
        readonly UniformGrid _grid = new UniformGrid { Rows = 3, Columns = 3 };
        readonly List<(DesktopItem item, Image img)> _minis = new List<(DesktopItem, Image)>();

        public FolderTile(LauncherEntry entry, TileMetrics m, bool effects, Launcher launcher) : base(entry, m, effects)
        {
            _launcher = launcher;
            Plate.Background = FolderFill;
            PlateContent.Children.Add(_grid);
            Refresh();
            ApplyMetrics(m);
        }

        public override void ApplyMetrics(TileMetrics m)
        {
            base.ApplyMetrics(m);
            _grid.Margin = new Thickness(Math.Round(m.Icon * 0.12));
            foreach (var (_, img) in _minis) img.Margin = new Thickness(Math.Max(1, m.Icon * 0.025));
        }

        public override void Refresh()
        {
            Detach();
            _grid.Children.Clear();
            _minis.Clear();
            double margin = Math.Max(1, (Metrics?.Icon ?? 64) * 0.025);
            foreach (var it in _launcher.ItemsOf(Entry.Group).Take(9))
            {
                var img = new Image { Source = it.Icon, Stretch = Stretch.Uniform, Margin = new Thickness(margin) };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                _grid.Children.Add(img);
                _minis.Add((it, img));
                it.PropertyChanged += OnMiniChanged;
            }
            SetLabel(Entry.Group.Name);
        }

        void OnMiniChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(DesktopItem.Icon)) return;
            foreach (var (item, img) in _minis) if (item == sender) img.Source = item.Icon;
        }

        public override void Detach()
        {
            foreach (var (item, _) in _minis) item.PropertyChanged -= OnMiniChanged;
        }
    }
}
