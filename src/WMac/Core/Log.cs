using System;
using System.IO;

namespace WMac
{
    internal static class Log
    {
        static readonly object Gate = new object();

        /// <summary>Carpeta de datos propia de WMac (ajustes y registro). Nunca se escribe fuera de ella.</summary>
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WMac");

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DataDir);
                    string file = Path.Combine(DataDir, "wmac.log");
                    var fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
                    File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\r\n");
                }
            }
            catch { }
        }

        public static void Error(string context, Exception ex) => Write($"ERROR {context}: {ex.GetType().Name}: {ex.Message}\r\n{ex.StackTrace}");
    }
}
