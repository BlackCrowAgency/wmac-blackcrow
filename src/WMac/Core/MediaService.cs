using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WMac
{
    internal sealed class MediaInfo
    {
        public bool Available;          // la API del sistema está disponible
        public bool HasSession;         // hay una app reproduciendo (o pausada)
        public string Title, Artist, AppName;
        public BitmapSource Art;
        public Color? ArtColor;
        public bool IsPlaying, CanPlayPause, CanNext, CanPrevious;
        public TimeSpan Position, Duration;
        public DateTime PositionAt = DateTime.Now;

        public TimeSpan EstimatedPosition
        {
            get
            {
                var p = IsPlaying ? Position + (DateTime.Now - PositionAt) : Position;
                if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
                return p < TimeSpan.Zero ? TimeSpan.Zero : p;
            }
        }
    }

    /// <summary>
    /// Datos y controles REALES de la reproducción actual mediante los controles multimedia de Windows
    /// (Spotify, navegadores, Reproductor multimedia…). Si no hay nada reproduciéndose, lo indica: no simula nada.
    /// </summary>
    internal sealed class MediaService
    {
        public static MediaService Shared { get; } = new MediaService();

        GlobalSystemMediaTransportControlsSessionManager _manager;
        GlobalSystemMediaTransportControlsSession _session;
        Dispatcher _ui;
        bool _started;
        int _version;

        public MediaInfo Current { get; private set; } = new MediaInfo { Available = true };
        public event Action Updated;

        public async void Start(Dispatcher ui)
        {
            if (_started) return;
            _started = true;
            _ui = ui;
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                _manager.CurrentSessionChanged += (s, e) => Post(Rebind);
                Rebind();
            }
            catch (Exception ex)
            {
                Log.Write("media: " + ex.Message);
                Current = new MediaInfo { Available = false };
                Updated?.Invoke();
            }
        }

        public void Stop()
        {
            Unbind();
            _manager = null;
        }

        void Post(Action a) => _ui?.BeginInvoke(a);

        void Unbind()
        {
            if (_session == null) return;
            try
            {
                _session.MediaPropertiesChanged -= OnMediaProperties;
                _session.PlaybackInfoChanged -= OnPlaybackInfo;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            catch { }
            _session = null;
        }

        void Rebind()
        {
            Unbind();
            _title = _artist = null;
            _art = null;
            _artColor = null;
            try { _session = _manager?.GetCurrentSession(); } catch { _session = null; }
            if (_session != null)
            {
                _session.MediaPropertiesChanged += OnMediaProperties;
                _session.PlaybackInfoChanged += OnPlaybackInfo;
                _session.TimelinePropertiesChanged += OnTimeline;
            }
            Refresh(true);
        }

        void OnMediaProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => Post(() => Refresh(true));
        void OnPlaybackInfo(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => Post(() => Refresh(false));
        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => Post(() => Refresh(false));

        // Última información de pista conocida de la sesión actual (se carga de forma asíncrona).
        string _title, _artist;
        BitmapSource _art;
        Color? _artColor;

        async void Refresh(bool withProperties)
        {
            var session = _session;
            if (session == null)
            {
                _title = _artist = null; _art = null; _artColor = null;
                Current = new MediaInfo { Available = true };
                Updated?.Invoke();
                return;
            }

            if (withProperties)
            {
                // Contador propio: una actualización de reproducción no cancela la carga de la nueva pista.
                int version = ++_version;
                string title = null, artist = null;
                BitmapSource art = null;
                try
                {
                    var props = await session.TryGetMediaPropertiesAsync();
                    title = props?.Title;
                    artist = props?.Artist;
                    if (props?.Thumbnail != null) art = await LoadArt(props.Thumbnail);
                }
                catch (Exception ex) { Log.Write("media props: " + ex.Message); }
                if (version != _version || session != _session) return;   // llegó una pista más reciente
                _title = title;
                _artist = artist;
                _art = art;
                _artColor = art != null ? AverageColor(art) : null;
            }
            if (session != _session) return;
            Current = Snapshot(session);
            Updated?.Invoke();
        }

        MediaInfo Snapshot(GlobalSystemMediaTransportControlsSession session)
        {
            var info = new MediaInfo
            {
                Available = true,
                HasSession = true,
                AppName = FriendlyAppName(session.SourceAppUserModelId),
                Title = _title,
                Artist = _artist,
                Art = _art,
                ArtColor = _artColor
            };
            try
            {
                var pb = session.GetPlaybackInfo();
                info.IsPlaying = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                var c = pb.Controls;
                info.CanPlayPause = c.IsPlayPauseToggleEnabled || c.IsPlayEnabled || c.IsPauseEnabled;
                info.CanNext = c.IsNextEnabled;
                info.CanPrevious = c.IsPreviousEnabled;
            }
            catch { }
            try
            {
                var tl = session.GetTimelineProperties();
                info.Duration = tl.EndTime - tl.StartTime;
                info.Position = tl.Position - tl.StartTime;
                var updated = tl.LastUpdatedTime.LocalDateTime;
                info.PositionAt = updated.Year > 2000 && updated <= DateTime.Now ? updated : DateTime.Now;
            }
            catch { }
            return info;
        }

        static async Task<BitmapSource> LoadArt(IRandomAccessStreamReference reference)
        {
            using (var ras = await reference.OpenReadAsync())
            using (var stream = ras.AsStreamForRead())
            {
                var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                if (ms.Length == 0) return null;
                ms.Position = 0;
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.StreamSource = ms;
                bi.DecodePixelWidth = 420;
                bi.EndInit();
                bi.Freeze();
                return bi;
            }
        }

        static Color? AverageColor(BitmapSource src)
        {
            try
            {
                BitmapSource s = new TransformedBitmap(src, new ScaleTransform(8.0 / src.PixelWidth, 8.0 / src.PixelHeight));
                s = new FormatConvertedBitmap(s, PixelFormats.Bgra32, null, 0);
                var px = new byte[s.PixelWidth * s.PixelHeight * 4];
                s.CopyPixels(px, s.PixelWidth * 4, 0);
                long r = 0, g = 0, b = 0, n = 0;
                for (int i = 0; i < px.Length; i += 4) { b += px[i]; g += px[i + 1]; r += px[i + 2]; n++; }
                return n == 0 ? (Color?)null : Color.FromRgb((byte)(r / n), (byte)(g / n), (byte)(b / n));
            }
            catch { return null; }
        }

        static string FriendlyAppName(string aumid)
        {
            string a = (aumid ?? "").ToLowerInvariant();
            if (a.Contains("spotify")) return "Spotify";
            if (a.Contains("chrome")) return "Chrome";
            if (a.Contains("msedge") || a.Contains("microsoftedge")) return "Edge";
            if (a.Contains("firefox")) return "Firefox";
            if (a.Contains("opera")) return "Opera";
            if (a.Contains("brave")) return "Brave";
            if (a.Contains("zunemusic") || a.Contains("mediaplayer")) return "Reproductor multimedia";
            if (a.Contains("vlc")) return "VLC";
            if (a.Contains("applemusic") || a.Contains("itunes")) return "Apple Music";
            if (a.Contains("deezer")) return "Deezer";
            if (a.Contains("tidal")) return "TIDAL";
            string s = aumid ?? "";
            int bang = s.IndexOf('!');
            if (bang > 0) s = s.Substring(0, bang);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            int us = s.IndexOf('_');
            if (us > 0) s = s.Substring(0, us);
            int dot = s.LastIndexOf('.');
            if (dot >= 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            return s;
        }

        public async void TogglePlayPause()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TryTogglePlayPauseAsync(); } catch (Exception ex) { Log.Write("media play: " + ex.Message); }
        }

        public async void Next()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipNextAsync(); } catch (Exception ex) { Log.Write("media next: " + ex.Message); }
        }

        public async void Previous()
        {
            var s = _session;
            if (s == null) return;
            try { await s.TrySkipPreviousAsync(); } catch (Exception ex) { Log.Write("media prev: " + ex.Message); }
        }
    }
}
