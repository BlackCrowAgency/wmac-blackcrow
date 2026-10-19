using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WMac
{
    /// <summary>
    /// Dónde viven los respaldos de la distribución del escritorio (%LOCALAPPDATA%\WMac\Respaldos).
    /// • original.json / original.reg: el primero que se tomó, antes de activar WMac por primera vez. Nunca se sobrescribe.
    /// • respaldo-AAAAMMDD-HHMMSS.json: uno antes de cada activación (se guardan los 15 últimos).
    /// • sesion-activa.txt: existe mientras WMac está integrado; si sigue ahí al iniciar, la sesión anterior terminó de golpe.
    /// La desinstalación conserva esta carpeta.
    /// </summary>
    internal static class LayoutBackups
    {
        public const string FolderName = "Respaldos";
        public static string Dir => Path.Combine(Log.DataDir, FolderName);
        public static string OriginalPath => Path.Combine(Dir, "original.json");
        static string SessionPath => Path.Combine(Dir, "sesion-activa.txt");
        const int Keep = 15;

        /// <summary>Guarda el respaldo. El primero de todos se conserva además como «original».</summary>
        public static string Save(LayoutSnapshot s)
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, $"respaldo-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            WriteAtomic(path, Serialize(s));
            if (!File.Exists(OriginalPath))
            {
                var original = Serialize(s);
                WriteAtomic(OriginalPath, original);
                WriteAtomic(Path.Combine(Dir, "original.reg"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(RegValue.ToRegFile(DesktopLayout.BagsKey, s.Registry))).ToArray());
                WriteReadme();
                Log.Write($"Respaldo ORIGINAL del escritorio guardado: {s.Icons.Count} iconos, {s.Files.Count} archivos");
            }
            WriteAtomic(Path.Combine(Dir, "ultimo.reg"), Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(RegValue.ToRegFile(DesktopLayout.BagsKey, s.Registry))).ToArray());
            Prune();
            s.FilePath = path;
            Log.Write($"Respaldo del escritorio guardado ({s.Reason}): {Path.GetFileName(path)}, {s.Icons.Count} iconos");
            return path;
        }

        public static LayoutSnapshot Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                using (var fs = File.OpenRead(path))
                {
                    var s = Serializer().ReadObject(fs) as LayoutSnapshot;
                    if (s != null) s.FilePath = path;
                    return s;
                }
            }
            catch (Exception ex) { Log.Error("leer respaldo " + path, ex); return null; }
        }

        public static LayoutSnapshot Original() => Load(OriginalPath);

        /// <summary>El respaldo más reciente (o el original si no hay otro).</summary>
        public static LayoutSnapshot Latest() => Load(Files().FirstOrDefault()) ?? Original();

        static IEnumerable<string> Files()
        {
            if (!Directory.Exists(Dir)) return Enumerable.Empty<string>();
            return Directory.GetFiles(Dir, "respaldo-*.json").OrderByDescending(f => f, StringComparer.Ordinal);
        }

        static void Prune()
        {
            foreach (var old in Files().Skip(Keep))
                try { File.Delete(old); } catch (Exception ex) { Log.Error("limpiar respaldos", ex); }
        }

        // ---------- sesión integrada ----------

        public static void BeginSession(string backupPath)
        {
            try { Directory.CreateDirectory(Dir); File.WriteAllText(SessionPath, backupPath ?? ""); }
            catch (Exception ex) { Log.Error("marcar sesión", ex); }
        }

        public static void EndSession()
        {
            try { if (File.Exists(SessionPath)) File.Delete(SessionPath); }
            catch (Exception ex) { Log.Error("cerrar sesión", ex); }
        }

        /// <summary>Respaldo de una sesión que no terminó limpiamente (WMac se cerró de golpe o se apagó el equipo sin cerrarlo).</summary>
        public static string PendingSession()
        {
            try { return File.Exists(SessionPath) ? File.ReadAllText(SessionPath).Trim() : null; }
            catch { return null; }
        }

        // ---------- archivos ----------

        static byte[] Serialize(LayoutSnapshot s)
        {
            using (var ms = new MemoryStream())
            {
                Serializer().WriteObject(ms, s);
                return ms.ToArray();
            }
        }

        static DataContractJsonSerializer Serializer() =>
            new DataContractJsonSerializer(typeof(LayoutSnapshot), new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });

        static void WriteAtomic(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        static void WriteReadme()
        {
            string text =
                "RESPALDO DE LA DISTRIBUCIÓN DE TU ESCRITORIO (creado por WMac)\r\n" +
                "==============================================================\r\n\r\n" +
                "WMac guarda aquí cómo estaba tu escritorio de Windows ANTES de activarse: la posición de cada icono,\r\n" +
                "el tamaño y el orden de los iconos, «Organizar automáticamente», «Alinear iconos a la cuadrícula»\r\n" +
                "y la lista de archivos del Escritorio. Esta carpeta se conserva aunque desinstales WMac.\r\n\r\n" +
                "  original.json  El primer respaldo, tomado antes de la primera activación. Nunca se sobrescribe.\r\n" +
                "  original.reg   Lo mismo, tal como lo guarda Windows en el registro (clave Shell\\Bags\\1\\Desktop).\r\n" +
                "  respaldo-*.json  Un respaldo antes de cada activación (los 15 últimos).\r\n" +
                "  ultimo.reg     El registro del respaldo más reciente.\r\n\r\n" +
                "RESTAURAR CON WMAC (recomendado)\r\n" +
                "  Menú Inicio › WMac › Restaurar escritorio original: compara tu escritorio con el respaldo y, si algo\r\n" +
                "  cambió, te ofrece devolver cada icono a su sitio. También: WMac.exe --restore-layout\r\n\r\n" +
                "RESTAURAR SIN WMAC (si ya lo desinstalaste)\r\n" +
                "  1. Abre el Administrador de tareas (Ctrl+Mayús+Esc) › Ejecutar nueva tarea › escribe cmd › Aceptar.\r\n" +
                "  2. En esa ventana escribe, una línea cada vez:\r\n" +
                "       taskkill /f /im explorer.exe\r\n" +
                "       reg import \"%LOCALAPPDATA%\\WMac\\Respaldos\\original.reg\"\r\n" +
                "       start explorer.exe\r\n" +
                "  Cerrar Explorer con /f evita que guarde encima la distribución actual. Al volver a abrirse, lee la del respaldo.\r\n";
            File.WriteAllText(Path.Combine(Dir, "LEEME.txt"), text, Encoding.UTF8);
        }
    }
}
