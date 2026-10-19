using System;
using System.Windows;

namespace WMac
{
    /// <summary>
    /// Órdenes de línea de comandos y de los accesos del menú Inicio para el respaldo del escritorio.
    /// Funcionan aunque WMac no esté abierto. Guardar y comprobar son de solo lectura; restaurar siempre pregunta antes.
    /// </summary>
    internal static class LayoutCommands
    {
        const string Title = "WMac";

        /// <summary>--backup-layout: guarda un respaldo ahora.</summary>
        public static void Backup()
        {
            var snap = DesktopLayout.TryCapture("manual", out string error);
            if (snap == null) { Box("No se pudo leer tu escritorio ahora mismo.\n\n" + error, MessageBoxImage.Warning); return; }
            try
            {
                string path = LayoutBackups.Save(snap);
                Box($"Se guardó la posición de tus {snap.Icons.Count} iconos.\n\n{path}", MessageBoxImage.Information);
            }
            catch (Exception ex) { Box("No se pudo guardar el respaldo.\n\n" + ex.Message, MessageBoxImage.Warning); }
        }

        /// <summary>--check-layout: compara el escritorio con el respaldo más reciente y, si no coincide, ofrece restaurarlo.</summary>
        public static void Check() => CheckAgainst(LayoutBackups.Latest(), null);

        /// <summary>
        /// Compara con <paramref name="baseline"/> y ofrece restaurar si hace falta. Devuelve un resumen para
        /// mensajes compuestos (p. ej. «Restaurar escritorio original»); si <paramref name="lead"/> no es null, se antepone.
        /// </summary>
        public static void CheckAgainst(LayoutSnapshot baseline, string lead)
        {
            string head = lead != null ? lead + "\n\n" : "";
            if (baseline == null)
            {
                Box(head + "Aún no hay respaldo: WMac nunca llegó a activarse.", MessageBoxImage.Information);
                return;
            }
            var now = DesktopLayout.TryCapture("comprobación", out string error);
            if (now == null) { Box(head + "No se pudo comparar tu escritorio ahora mismo.\n\n" + error, MessageBoxImage.Warning); return; }
            var diff = DesktopLayout.Compare(baseline, now);
            Log.Write($"Comprobación (línea de comandos) frente al respaldo del {baseline.Taken}: {diff.Describe().Replace("\n", " ")}");
            string body = head + diff.Describe();
            if (diff.LayoutIntact) { Box(body, MessageBoxImage.Information); return; }
            if (Ask(body + "\n\n¿Devolver tus iconos a su sitio? Tus archivos no se tocan."))
                RestoreFrom(baseline);
        }

        /// <summary>--restore-layout [archivo]: restaura (por defecto, el respaldo más reciente) tras confirmar.</summary>
        public static void Restore(string file)
        {
            var s = file != null ? LayoutBackups.Load(file) : LayoutBackups.Latest();
            if (s == null) { Box(file != null ? "No se pudo leer ese respaldo:\n" + file : "No hay ningún respaldo guardado todavía.", MessageBoxImage.Warning); return; }
            if (!Ask($"¿Devolver tus iconos a como estaban el {s.When}?\n\nTus archivos no se tocan."))
                return;
            RestoreFrom(s);
        }

        static void RestoreFrom(LayoutSnapshot s)
        {
            try { Box(DesktopLayout.Restore(s).Describe(), MessageBoxImage.Information); }
            catch (Exception ex)
            {
                Log.Error("restaurar distribución", ex);
                Box("No se pudo terminar de restaurar. Inténtalo de nuevo más tarde.\n\n" + ex.Message, MessageBoxImage.Warning);
            }
        }

        static void Box(string text, MessageBoxImage icon) => MessageBox.Show(text, Title, MessageBoxButton.OK, icon);
        static bool Ask(string text) => MessageBox.Show(text, Title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    }
}
