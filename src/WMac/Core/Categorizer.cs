using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WMac
{
    /// <summary>Categorías visuales (solo afectan a cómo WMac muestra los elementos).</summary>
    internal enum Category { Aplicaciones, Internet, Desarrollo, Utilidades, Juegos, Carpetas, Documentos }

    /// <summary>Clasifica automáticamente cada elemento por su tipo, nombre y destino del acceso directo.</summary>
    internal static class Categorizer
    {
        public static readonly Category[] Order =
            { Category.Aplicaciones, Category.Internet, Category.Desarrollo, Category.Utilidades, Category.Juegos, Category.Carpetas, Category.Documentos };

        public static int Rank(Category c) => Array.IndexOf(Order, c);

        static readonly string[] GamePathMarkers =
        {
            "steamapps", "\\steam\\", "epic games", "riot games", "gog galaxy", "gog games", "\\ubisoft", "\\ea games",
            "electronic arts", "origin games", "battle.net", "blizzard", "rockstar games", "\\xboxgames\\", "\\games\\",
            "\\juegos\\", "roblox", "minecraft", "bluestacks", "\\nox\\", "noxplayer", "ldplayer", "memu"
        };

        static readonly string[] GameWords =
        {
            "steam", "epic games", "ea", "ea app", "origin", "battle net", "gog galaxy", "ubisoft connect", "uplay", "riot client",
            "valorant", "league of legends", "xbox", "roblox", "minecraft", "bluestacks", "noxplayer", "nox", "ldplayer",
            "memu", "mumu", "playnite", "rockstar games launcher", "game", "games", "juego", "juegos"
        };

        static readonly string[] DevWords =
        {
            "visual studio", "vs code", "vscode", "jetbrains", "intellij", "pycharm", "webstorm", "rider", "clion", "goland",
            "phpstorm", "datagrip", "android studio", "git", "git bash", "github", "github desktop", "gitkraken", "sourcetree",
            "docker", "postman", "insomnia", "node js", "nodejs", "python", "idle", "anaconda", "jupyter", "spyder", "rstudio",
            "r", "unity", "unity hub", "unreal engine", "godot", "sublime text", "notepad", "cursor", "windsurf", "warp",
            "terminal", "powershell", "cmd", "simbolo del sistema", "wsl", "ubuntu", "arduino", "matlab", "dbeaver", "mysql",
            "workbench", "pgadmin", "xampp", "wamp", "laragon", "meta spark", "spark ar", "filezilla", "putty", "winscp",
            "heidisql", "eclipse", "netbeans", "code"
        };

        static readonly string[] NetPathMarkers =
            { "\\chrome.exe", "msedge", "firefox.exe", "\\opera", "brave.exe", "tor browser", "whatsapp", "telegram", "discord", "signal" };

        static readonly string[] NetWords =
        {
            "chrome", "google chrome", "edge", "microsoft edge", "firefox", "opera", "opera gx", "brave", "vivaldi", "tor browser",
            "duckduckgo", "chromium", "whatsapp", "wa business", "telegram", "signal", "discord", "slack", "teams", "zoom",
            "skype", "messenger", "facebook", "instagram", "twitter", "tiktok", "thunderbird", "outlook", "correo", "mail",
            "windscribe", "nordvpn", "protonvpn", "proton vpn", "expressvpn", "surfshark", "openvpn", "wireguard", "cloudflare",
            "qbittorrent", "utorrent", "bittorrent", "internet download manager", "idm", "jdownloader", "free download manager",
            "netflix", "youtube", "twitch"
        };

        static readonly string[] UtilWords =
        {
            "nvidia", "geforce", "geforce experience", "nvidia app", "amd", "radeon", "intel", "logitech", "logi", "razer",
            "corsair", "icue", "afterburner", "cpu z", "gpu z", "hwmonitor", "hwinfo", "crystaldiskinfo", "crystaldisk",
            "ccleaner", "7 zip", "winrar", "peazip", "poweriso", "anyburn", "imgburn", "daemon tools", "rufus", "balenaetcher",
            "ventoy", "teamviewer", "anydesk", "rustdesk", "everything", "powertoys", "revo", "iobit", "malwarebytes", "avast",
            "avg", "kaspersky", "bitdefender", "eset", "norton", "mcafee", "panel de control", "control panel", "configuracion",
            "settings", "calculadora", "calculator", "apkmirror", "installer", "instalador", "uninstall", "desinstalar", "driver",
            "drivers", "recortes", "snipping tool"
        };

        static readonly HashSet<string> DevExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".py", ".pyw", ".ipynb", ".js", ".mjs", ".ts", ".tsx", ".jsx", ".java", ".kt", ".c", ".h", ".cpp", ".hpp", ".cs",
            ".go", ".rs", ".php", ".rb", ".swift", ".sh", ".ps1", ".bat", ".cmd", ".json", ".xml", ".yml", ".yaml", ".toml",
            ".sql", ".r", ".html", ".htm", ".css", ".scss", ".spec", ".vue", ".dart", ".lua", ".gradle", ".sln", ".csproj"
        };

        public static Category Classify(DesktopItem it)
        {
            if (it.IsSpecial) return Category.Utilidades;
            if (it.IsFolder) return Category.Carpetas;

            string ext = it.Extension;
            string name = Words(it.Name);
            string target = (it.Target ?? "").ToLowerInvariant();
            bool shortcut = ext == ".lnk" || ext == ".url" || ext == ".appref-ms";

            if (!shortcut)
            {
                if (ext == ".exe" || ext == ".msi")
                    return ByPath(it.Path.ToLowerInvariant()) ?? ByWords(name) ?? Category.Aplicaciones;
                return DevExt.Contains(ext) ? Category.Desarrollo : Category.Documentos;
            }

            if (ext == ".url")
            {
                if (target.StartsWith("steam://") || target.StartsWith("com.epicgames.launcher://") || target.StartsWith("uplay://") ||
                    target.StartsWith("origin") || target.StartsWith("battlenet://") || target.StartsWith("roblox") ||
                    target.StartsWith("goggalaxy://") || target.StartsWith("riotclient"))
                    return Category.Juegos;
                return ByWords(name) ?? (target.StartsWith("http") ? Category.Internet : Category.Aplicaciones);
            }

            return ByPath(target) ?? ByWords(name + " " + Words(Path.GetFileNameWithoutExtension(target))) ?? Category.Aplicaciones;
        }

        static Category? ByPath(string target)
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (GamePathMarkers.Any(target.Contains)) return Category.Juegos;
            if (NetPathMarkers.Any(target.Contains)) return Category.Internet;
            return null;
        }

        static Category? ByWords(string words)
        {
            string padded = " " + words + " ";
            bool Has(string[] list) => list.Any(k => padded.Contains(" " + k + " "));
            if (Has(GameWords)) return Category.Juegos;
            if (Has(DevWords)) return Category.Desarrollo;
            if (Has(NetWords)) return Category.Internet;
            if (Has(UtilWords)) return Category.Utilidades;
            return null;
        }

        /// <summary>Minúsculas sin tildes; todo lo que no sea letra o número pasa a ser espacio.</summary>
        public static string Words(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s.Normalize(NormalizationForm.FormD))
            {
                var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch)) { sb.Append(char.ToLowerInvariant(ch)); space = false; }
                else if (!space && sb.Length > 0) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }
    }
}
