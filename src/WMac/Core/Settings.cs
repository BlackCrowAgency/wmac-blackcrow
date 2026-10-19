using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Threading;

namespace WMac
{
    [DataContract]
    public sealed class AppSettings
    {
        /// <summary>El usuario activó el escritorio WMac (se integra al iniciar).</summary>
        [DataMember] public bool DesktopEnabled;
        /// <summary>El usuario confirmó que la integración se ve bien (diálogo con cuenta atrás).</summary>
        [DataMember] public bool DesktopConfirmed;
        [DataMember] public bool WelcomeShown;

        [DataMember] public string IconSize;          // small | medium | large | xlarge
        [DataMember] public bool ShowIcons;
        [DataMember] public bool ShowClock;
        [DataMember] public bool ShowSeconds;
        [DataMember] public bool ShowDate;
        [DataMember] public bool ShowCalendar;
        [DataMember] public bool ShowWeather;
        [DataMember] public bool ShowMusic;

        /// <summary>
        /// Protección de archivos (activada por defecto): WMac solo abre y copia; no elimina, renombra, mueve ni crea nada.
        /// Arrastrar un icono a otra ventana lo copia, nunca lo mueve.
        /// </summary>
        [DataMember] public bool ProtectFiles;

        /// <summary>Organización virtual del lanzador (orden, páginas y grupos). Nunca afecta a los archivos.</summary>
        [DataMember] public LauncherLayout Launcher;

        [DataMember] public string WeatherQuery;
        [DataMember] public string WeatherPlace;
        [DataMember] public double WeatherLat;
        [DataMember] public double WeatherLon;
        [DataMember] public bool WeatherFahrenheit;

        /// <summary>Posición de cada icono: clave = ruta en minúsculas, valor = "columna,fila" (columna 0 = derecha).</summary>
        [DataMember] public Dictionary<string, string> Layout;

        public AppSettings() { Defaults(); }

        [OnDeserializing] void OnDeserializing(StreamingContext c) => Defaults();

        [OnDeserialized]
        void OnDeserialized(StreamingContext c)
        {
            Layout = Layout == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(Layout, StringComparer.OrdinalIgnoreCase);
            if (IconSize != "small" && IconSize != "large" && IconSize != "xlarge") IconSize = "medium";
            WeatherQuery = WeatherQuery ?? "";
            WeatherPlace = WeatherPlace ?? "";
            Launcher = Launcher ?? new LauncherLayout();
            Launcher.Fix();
        }

        void Defaults()
        {
            IconSize = "medium";
            ShowIcons = true;
            ShowClock = true;
            ShowDate = true;
            ShowCalendar = true;
            ShowWeather = true;
            ShowMusic = true;
            ProtectFiles = true;
            Launcher = new LauncherLayout();
            WeatherQuery = "";
            WeatherPlace = "";
            Layout = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public bool WeatherConfigured => !string.IsNullOrEmpty(WeatherPlace) && !(WeatherLat == 0 && WeatherLon == 0);
    }

    internal static class SettingsStore
    {
        static DispatcherTimer _saveTimer;

        public static AppSettings Current { get; private set; } = new AppSettings();

        /// <summary>Para herramientas de diagnóstico: impide escribir en disco.</summary>
        public static bool ReadOnly { get; set; }

        public static event Action Changed;

        public static string FilePath => Path.Combine(Log.DataDir, "settings.json");

        static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(AppSettings), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        public static void Load()
        {
            Current = LoadFromDisk() ?? new AppSettings();
        }

        public static AppSettings LoadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                using (var fs = File.OpenRead(FilePath))
                    return Serializer().ReadObject(fs) as AppSettings;
            }
            catch (Exception ex)
            {
                Log.Error("settings load", ex);
                return null;
            }
        }

        public static void Save() => SaveTo(Current);

        public static void SaveTo(AppSettings s)
        {
            if (ReadOnly || s == null) return;
            try
            {
                Directory.CreateDirectory(Log.DataDir);
                string tmp = FilePath + ".tmp";
                using (var fs = File.Create(tmp))
                    Serializer().WriteObject(fs, s);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Log.Error("settings save", ex); }
        }

        public static void SaveSoon()
        {
            if (_saveTimer == null)
            {
                _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
                _saveTimer.Tick += (s, e) => { _saveTimer.Stop(); Save(); };
            }
            _saveTimer.Stop();
            _saveTimer.Start();
        }

        /// <summary>Notifica a la interfaz un cambio hecho por el usuario y lo guarda.</summary>
        public static void NotifyChanged()
        {
            Changed?.Invoke();
            SaveSoon();
        }

        public static void Flush()
        {
            if (_saveTimer != null && _saveTimer.IsEnabled) { _saveTimer.Stop(); }
            Save();
        }
    }
}
