using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WMac
{
    /// <summary>
    /// Tarjeta "Liquid Glass": muestra la porción desenfocada del fondo que tiene detrás, más tinte,
    /// brillo especular y borde luminoso. El desenfoque se calcula una sola vez por fondo, así que
    /// el coste en reposo es nulo.
    /// </summary>
    public class GlassCard : ContentControl
    {
        public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
            nameof(CornerRadius), typeof(CornerRadius), typeof(GlassCard), new FrameworkPropertyMetadata(new CornerRadius(24)));

        public static readonly DependencyProperty BackdropProperty = DependencyProperty.RegisterAttached(
            "Backdrop", typeof(ImageSource), typeof(GlassCard),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, e) => (d as GlassCard)?.UpdateBackdrop()));

        public static readonly DependencyProperty BackdropHostProperty = DependencyProperty.RegisterAttached(
            "BackdropHost", typeof(FrameworkElement), typeof(GlassCard),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, (d, e) => (d as GlassCard)?.UpdateBackdrop()));

        public static void SetBackdrop(DependencyObject d, ImageSource v) => d.SetValue(BackdropProperty, v);
        public static ImageSource GetBackdrop(DependencyObject d) => (ImageSource)d.GetValue(BackdropProperty);
        public static void SetBackdropHost(DependencyObject d, FrameworkElement v) => d.SetValue(BackdropHostProperty, v);
        public static FrameworkElement GetBackdropHost(DependencyObject d) => (FrameworkElement)d.GetValue(BackdropHostProperty);

        public CornerRadius CornerRadius
        {
            get => (CornerRadius)GetValue(CornerRadiusProperty);
            set => SetValue(CornerRadiusProperty, value);
        }

        Border _part;
        ImageBrush _brush;
        Rect _lastViewbox = Rect.Empty;

        public GlassCard()
        {
            Loaded += (s, e) => { LayoutUpdated += OnLayoutUpdated; UpdateBackdrop(); };
            Unloaded += (s, e) => LayoutUpdated -= OnLayoutUpdated;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _part = GetTemplateChild("PART_Backdrop") as Border;
            _brush = null;
            UpdateBackdrop();
        }

        void UpdateBackdrop()
        {
            if (_part == null) return;
            var src = GetBackdrop(this) as BitmapSource;
            if (src == null || GetBackdropHost(this) == null) return;
            if (_brush == null || _brush.ImageSource != src)
            {
                _brush = new ImageBrush(src) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
                _part.Background = _brush;
                _lastViewbox = Rect.Empty;
            }
            UpdateViewbox();
        }

        void OnLayoutUpdated(object sender, EventArgs e) => UpdateViewbox();

        void UpdateViewbox()
        {
            var host = GetBackdropHost(this);
            if (_brush == null || _part == null || host == null || host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
            if (!(_brush.ImageSource is BitmapSource src) || !host.IsAncestorOf(_part)) return;
            Point p;
            try { p = _part.TransformToAncestor(host).Transform(new Point(0, 0)); }
            catch (InvalidOperationException) { return; }
            double sx = src.PixelWidth / host.ActualWidth, sy = src.PixelHeight / host.ActualHeight;
            var vb = new Rect(p.X * sx, p.Y * sy, Math.Max(1, _part.ActualWidth * sx), Math.Max(1, _part.ActualHeight * sy));
            if (vb == _lastViewbox) return;
            _lastViewbox = vb;
            _brush.Viewbox = vb;
        }
    }
}
