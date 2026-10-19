using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Threading;

namespace WMac
{
    internal enum WeatherKind { Clear, PartlyCloudy, Cloudy, Fog, Drizzle, Rain, Snow, Storm }

    internal sealed class WeatherData
    {
        public double Temp, Max, Min;
        public int Code;
        public bool IsDay = true;
        public DateTime Updated;
        public bool Fahrenheit;
        public List<HourForecast> Hours = new List<HourForecast>();
        public List<DayForecast> Days = new List<DayForecast>();
    }

    internal sealed class HourForecast { public DateTime Time; public double Temp; public int Code; public bool IsDay; }
    internal sealed class DayForecast { public DateTime Date; public double Max, Min; public int Code; }

    /// <summary>
    /// Clima opcional mediante Open-Meteo (gratuito, sin cuenta ni clave). Solo se consulta si el
    /// usuario configuró una ciudad; únicamente se envían las coordenadas de esa ciudad.
    /// </summary>
    internal sealed class WeatherService
    {
        public static WeatherService Shared { get; } = new WeatherService();

        static readonly HttpClient Http = CreateClient();
        DispatcherTimer _timer;
        bool _busy;

        public WeatherData Current { get; set; }
        public string Error { get; private set; }
        public event Action Updated;

        static HttpClient CreateClient()
        {
            var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            c.DefaultRequestHeaders.UserAgent.ParseAdd("WMac/1.0");
            return c;
        }

        public void Start()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
            _timer.Tick += (s, e) => Refresh();
            _timer.Start();
            Refresh();
        }

        public void Stop() => _timer?.Stop();

        public async void Refresh()
        {
            var s = SettingsStore.Current;
            if (_busy) return;
            if (!s.WeatherConfigured || !s.ShowWeather) { Updated?.Invoke(); return; }
            _busy = true;
            try
            {
                Current = await Fetch(s.WeatherLat, s.WeatherLon, s.WeatherFahrenheit);
                Error = null;
            }
            catch (Exception ex)
            {
                Error = "Sin conexión";
                Log.Write("weather: " + ex.Message);
            }
            finally { _busy = false; }
            Updated?.Invoke();
        }

        public static async Task<(double Lat, double Lon, string Place)?> Geocode(string query)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=es&format=json&name=" + Uri.EscapeDataString(query.Trim());
            var root = Parse<GeoResponse>(await Http.GetStringAsync(url));
            var first = root?.Results != null && root.Results.Length > 0 ? root.Results[0] : null;
            if (first == null) return null;
            string name = string.IsNullOrEmpty(first.Name) ? query : first.Name;
            return (first.Latitude, first.Longitude, string.IsNullOrEmpty(first.Country) ? name : $"{name}, {first.Country}");
        }

        static async Task<WeatherData> Fetch(double lat, double lon, bool fahrenheit)
        {
            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture) +
                         "&longitude=" + lon.ToString(CultureInfo.InvariantCulture) +
                         "&current=temperature_2m,weather_code,is_day&hourly=temperature_2m,weather_code,is_day" +
                         "&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=4" +
                         (fahrenheit ? "&temperature_unit=fahrenheit" : "");
            var root = Parse<ForecastResponse>(await Http.GetStringAsync(url));
            if (root?.Current == null) throw new InvalidDataException("respuesta de clima incompleta");
            var data = new WeatherData
            {
                Temp = root.Current.Temperature,
                Code = root.Current.WeatherCode,
                IsDay = root.Current.IsDay != 0,
                Max = root.Daily?.Max != null && root.Daily.Max.Length > 0 ? root.Daily.Max[0] : root.Current.Temperature,
                Min = root.Daily?.Min != null && root.Daily.Min.Length > 0 ? root.Daily.Min[0] : root.Current.Temperature,
                Updated = DateTime.Now,
                Fahrenheit = fahrenheit
            };

            // Próximas horas (en la hora local de la ciudad).
            DateTime now = ParseTime(root.Current.Time) ?? DateTime.Now;
            var h = root.Hourly;
            if (h?.Time != null && h.Temperature != null && h.Code != null)
                for (int i = 0; i < h.Time.Length && data.Hours.Count < 5; i++)
                {
                    var t = ParseTime(h.Time[i]);
                    if (t == null || t.Value <= now || i >= h.Temperature.Length || i >= h.Code.Length) continue;
                    data.Hours.Add(new HourForecast { Time = t.Value, Temp = h.Temperature[i], Code = h.Code[i], IsDay = h.IsDay == null || i >= h.IsDay.Length || h.IsDay[i] != 0 });
                }

            // Próximos días (sin contar hoy).
            var d = root.Daily;
            if (d?.Time != null && d.Max != null && d.Min != null && d.Code != null)
                for (int i = 1; i < d.Time.Length && data.Days.Count < 3; i++)
                {
                    if (i >= d.Max.Length || i >= d.Min.Length || i >= d.Code.Length) break;
                    if (!DateTime.TryParseExact(d.Time[i], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                    data.Days.Add(new DayForecast { Date = day, Max = d.Max[i], Min = d.Min[i], Code = d.Code[i] });
                }
            return data;
        }

        static DateTime? ParseTime(string s) =>
            DateTime.TryParseExact(s ?? "", "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : (DateTime?)null;

        static T Parse<T>(string json) where T : class
        {
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return new DataContractJsonSerializer(typeof(T)).ReadObject(ms) as T;
        }

#pragma warning disable 0649 // los rellena DataContractJsonSerializer
        [DataContract] sealed class GeoResponse { [DataMember(Name = "results")] public GeoResult[] Results; }

        [DataContract]
        sealed class GeoResult
        {
            [DataMember(Name = "name")] public string Name;
            [DataMember(Name = "country")] public string Country;
            [DataMember(Name = "latitude")] public double Latitude;
            [DataMember(Name = "longitude")] public double Longitude;
        }

        [DataContract]
        sealed class ForecastResponse
        {
            [DataMember(Name = "current")] public CurrentBlock Current;
            [DataMember(Name = "hourly")] public HourlyBlock Hourly;
            [DataMember(Name = "daily")] public DailyBlock Daily;
        }

        [DataContract]
        sealed class CurrentBlock
        {
            [DataMember(Name = "time")] public string Time;
            [DataMember(Name = "temperature_2m")] public double Temperature;
            [DataMember(Name = "weather_code")] public int WeatherCode;
            [DataMember(Name = "is_day")] public int IsDay;
        }

        [DataContract]
        sealed class HourlyBlock
        {
            [DataMember(Name = "time")] public string[] Time;
            [DataMember(Name = "temperature_2m")] public double[] Temperature;
            [DataMember(Name = "weather_code")] public int[] Code;
            [DataMember(Name = "is_day")] public int[] IsDay;
        }

        [DataContract]
        sealed class DailyBlock
        {
            [DataMember(Name = "time")] public string[] Time;
            [DataMember(Name = "weather_code")] public int[] Code;
            [DataMember(Name = "temperature_2m_max")] public double[] Max;
            [DataMember(Name = "temperature_2m_min")] public double[] Min;
        }
#pragma warning restore 0649

        public static WeatherKind KindOf(int code)
        {
            if (code == 0) return WeatherKind.Clear;
            if (code <= 2) return WeatherKind.PartlyCloudy;
            if (code == 3) return WeatherKind.Cloudy;
            if (code == 45 || code == 48) return WeatherKind.Fog;
            if (code >= 51 && code <= 57) return WeatherKind.Drizzle;
            if ((code >= 61 && code <= 67) || (code >= 80 && code <= 82)) return WeatherKind.Rain;
            if ((code >= 71 && code <= 77) || code == 85 || code == 86) return WeatherKind.Snow;
            if (code >= 95) return WeatherKind.Storm;
            return WeatherKind.Cloudy;
        }

        public static string Describe(int code)
        {
            switch (code)
            {
                case 0: return "Despejado";
                case 1: return "Mayormente despejado";
                case 2: return "Parcialmente nublado";
                case 3: return "Nublado";
                case 45: case 48: return "Niebla";
                case 51: case 53: case 55: return "Llovizna";
                case 56: case 57: return "Llovizna helada";
                case 61: return "Lluvia ligera";
                case 63: return "Lluvia";
                case 65: return "Lluvia intensa";
                case 66: case 67: return "Lluvia helada";
                case 71: case 73: case 75: return "Nieve";
                case 77: return "Granizo fino";
                case 80: case 81: return "Chubascos";
                case 82: return "Chubascos fuertes";
                case 85: case 86: return "Chubascos de nieve";
                case 95: return "Tormenta";
                case 96: case 99: return "Tormenta con granizo";
                default: return "—";
            }
        }
    }
}
