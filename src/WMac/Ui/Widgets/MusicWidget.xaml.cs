using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>Lo que suena ahora (controles multimedia de Windows). Sin reproducción, lo dice; no inventa nada.</summary>
    internal partial class MusicWidget : UserControl
    {
        static readonly Geometry PlayGeo = Geometry.Parse("M 0,0 L 0,20 L 17,10 Z");
        static readonly Geometry PauseGeo = Geometry.Parse("M 0,0 L 6,0 L 6,20 L 0,20 Z M 11,0 L 17,0 L 17,20 L 11,20 Z");

        readonly DispatcherTimer _progress = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        bool _started;

        public MusicWidget()
        {
            InitializeComponent();
            PlayButton.Click += (s, e) => MediaService.Shared.TogglePlayPause();
            NextButton.Click += (s, e) => MediaService.Shared.Next();
            PrevButton.Click += (s, e) => MediaService.Shared.Previous();
            _progress.Tick += (s, e) => UpdateProgress();
            Track.SizeChanged += (s, e) => UpdateProgress();
            Render();
        }

        public void Start()
        {
            if (_started) return;
            _started = true;
            MediaService.Shared.Updated += Render;
            Render();
        }

        public void Stop()
        {
            if (!_started) return;
            _started = false;
            MediaService.Shared.Updated -= Render;
            _progress.Stop();
        }

        void Render()
        {
            var m = MediaService.Shared.Current;
            bool has = m.HasSession && (!string.IsNullOrEmpty(m.Title) || m.Art != null);

            if (m.Art != null && has)
            {
                Art.Background = new ImageBrush(m.Art) { Stretch = Stretch.UniformToFill };
                Art.Visibility = Visibility.Visible;
            }
            else Art.Visibility = Visibility.Collapsed;

            SourceBadge.Visibility = has && !string.IsNullOrEmpty(m.AppName) ? Visibility.Visible : Visibility.Collapsed;
            SourceText.Text = m.AppName ?? "";

            if (!m.Available)
            {
                TitleText.Text = "Música no disponible";
                ArtistText.Text = "";
            }
            else if (!has)
            {
                TitleText.Text = "Nada en reproducción";
                ArtistText.Text = "Pon algo de música.";
            }
            else
            {
                TitleText.Text = string.IsNullOrEmpty(m.Title) ? "Sin título" : m.Title;
                ArtistText.Text = string.IsNullOrEmpty(m.Artist) ? (m.AppName ?? "") : m.Artist;
            }

            PlayIcon.Data = m.IsPlaying ? PauseGeo : PlayGeo;
            PlayButton.IsEnabled = has && m.CanPlayPause;
            NextButton.IsEnabled = has && m.CanNext;
            PrevButton.IsEnabled = has && m.CanPrevious;

            ProgressRow.Visibility = has && m.Duration > TimeSpan.FromSeconds(1) ? Visibility.Visible : Visibility.Hidden;
            UpdateProgress();
            if (_started && has && m.IsPlaying) _progress.Start(); else _progress.Stop();

            // Tinte sutil con el color de la portada (más oscuro para mantener el contraste del texto).
            if (has && m.ArtColor is Color c)
            {
                var tint = new SolidColorBrush(Color.FromArgb(0x66, (byte)(c.R * 0.55), (byte)(c.G * 0.55), (byte)(c.B * 0.55)));
                tint.Freeze();
                Card.Resources["GlassTint"] = tint;
            }
            else Card.Resources.Remove("GlassTint");
        }

        void UpdateProgress()
        {
            var m = MediaService.Shared.Current;
            if (m.Duration <= TimeSpan.Zero || Track.ActualWidth <= 0) { Fill.Width = 0; return; }
            var pos = m.EstimatedPosition;
            double ratio = Math.Max(0, Math.Min(1, pos.TotalSeconds / m.Duration.TotalSeconds));
            Fill.Width = Track.ActualWidth * ratio;
            ElapsedText.Text = Format(pos);
            RemainText.Text = "-" + Format(m.Duration - pos);
        }

        static string Format(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }
}
