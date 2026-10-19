using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WMac
{
    /// <summary>Instancia única, señales entre procesos, restauración y desinstalación.</summary>
    internal static class InstanceControl
    {
        public const string MutexName = @"Local\WMac.Desktop.Instance";
        public static readonly string[] Signals = { "Show", "Preview", "Exit", "Restore" };
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WMac";

        static string EventName(string signal) => @"Local\WMac.Desktop." + signal;

        public static bool IsRunning()
        {
            try { using (Mutex.OpenExisting(MutexName)) return true; }
            catch { return false; }
        }

        public static bool Signal(string signal)
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(EventName(signal))) { ev.Set(); return true; }
            }
            catch { return false; }
        }

        public static void Listen(Dispatcher dispatcher, Action<string> onSignal)
        {
            var handles = Signals.Select(s => (WaitHandle)new EventWaitHandle(false, EventResetMode.AutoReset, EventName(s))).ToArray();
            var t = new Thread(() =>
            {
                while (true)
                {
                    int i = WaitHandle.WaitAny(handles);
                    string name = Signals[i];
                    dispatcher.BeginInvoke((Action)(() => onSignal(name)));
                }
            }) { IsBackground = true, Name = "WMac.Signals" };
            t.Start();
        }

        /// <summary>Pide a la instancia en ejecución que se cierre; si no responde, la termina (su ventana desaparece con ella).</summary>
        public static void StopRunning(string signal, int timeoutMs = 6000)
        {
            Signal(signal);
            var sw = Stopwatch.StartNew();
            while (IsRunning() && sw.ElapsedMilliseconds < timeoutMs) Thread.Sleep(150);
            if (!IsRunning()) return;

            int me = Process.GetCurrentProcess().Id;
            foreach (var p in Process.GetProcessesByName("WMac"))
            {
                try { if (p.Id != me) { p.Kill(); p.WaitForExit(3000); } }
                catch (Exception ex) { Log.Error("kill", ex); }
            }
        }

        /// <summary>
        /// "Restaurar escritorio original": cierra WMac, desactiva su inicio automático y la integración, y después compara
        /// el escritorio con el respaldo tomado al activarlo (solo lectura). Si algo no coincide, ofrece devolver cada icono a su sitio.
        /// </summary>
        public static void RestoreFromCommandLine()
        {
            string pending = LayoutBackups.PendingSession();   // se lee antes: la instancia en marcha la cierra al salir
            if (IsRunning()) StopRunning("Restore");
            Autostart.Set(false);
            var s = SettingsStore.LoadFromDisk();
            // No se reactiva solo: hará falta activarlo y confirmar «Mantener» de nuevo.
            if (s != null) { s.DesktopEnabled = false; s.DesktopConfirmed = false; SettingsStore.SaveTo(s); }
            LayoutBackups.EndSession();
            var baseline = LayoutBackups.Load(pending) ?? LayoutBackups.Latest();
            LayoutCommands.CheckAgainst(baseline,
                "Volviste a tu escritorio original. WMac está cerrado y no se abrirá con Windows.");
        }

        public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WMac");
        public static string StartMenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WMac");

        /// <summary>Borra los datos de WMac (ajustes y registro) pero conserva la carpeta de respaldos del escritorio.</summary>
        static bool DeleteDataKeepingBackups()
        {
            try
            {
                if (!Directory.Exists(Log.DataDir)) return false;
                if (!Directory.Exists(LayoutBackups.Dir)) { Directory.Delete(Log.DataDir, true); return false; }
                foreach (var f in Directory.GetFiles(Log.DataDir)) try { File.Delete(f); } catch { }
                foreach (var d in Directory.GetDirectories(Log.DataDir))
                    if (!string.Equals(Path.GetFileName(d), LayoutBackups.FolderName, StringComparison.OrdinalIgnoreCase))
                        try { Directory.Delete(d, true); } catch { }
                return true;
            }
            catch { return Directory.Exists(LayoutBackups.Dir); }
        }

        public static void Uninstall()
        {
            var answer = MessageBox.Show(
                "¿Desinstalar WMac?\n\nTu escritorio y tus archivos no se tocan.",
                "Desinstalar WMac", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;

            string pending = LayoutBackups.PendingSession();
            if (IsRunning()) StopRunning("Exit");
            Autostart.Set(false);
            // Antes de irse, comprobar (solo lectura) que el escritorio esté como antes de activar WMac y ofrecer restaurarlo.
            var baseline = LayoutBackups.Load(pending) ?? LayoutBackups.Latest();
            if (baseline != null)
                LayoutCommands.CheckAgainst(baseline, "Antes de desinstalar, WMac comprueba que tus iconos sigan en su sitio.");
            LayoutBackups.EndSession();
            try { if (Directory.Exists(StartMenuDir)) Directory.Delete(StartMenuDir, true); } catch (Exception ex) { Log.Error("uninstall menu", ex); }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch (Exception ex) { Log.Error("uninstall key", ex); }
            bool kept = DeleteDataKeepingBackups();

            MessageBox.Show("WMac se desinstaló." + (kept
                ? "\n\nEl respaldo de tu escritorio se conserva en:\n" + LayoutBackups.Dir + "\n\nPuedes borrar esa carpeta cuando quieras."
                : ""), "WMac", MessageBoxButton.OK, MessageBoxImage.Information);

            // Solo se borra la carpeta de instalación propia de WMac, nunca otra ubicación.
            // Se espera a que este proceso termine (el exe está en uso mientras se ejecuta).
            string exeDir = Path.GetDirectoryName(Autostart.ExePath)?.TrimEnd('\\') ?? "";
            string installDir = InstallDir.TrimEnd('\\');
            if (string.Equals(exeDir, installDir, StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(installDir))
            {
                try
                {
                    string dir = installDir.Replace("'", "''");
                    int pid = Process.GetCurrentProcess().Id;
                    string script = $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; " +
                                    $"for ($i = 0; $i -lt 20 -and (Test-Path -LiteralPath '{dir}'); $i++) {{ " +
                                    $"Remove-Item -LiteralPath '{dir}' -Recurse -Force -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 500 }}";
                    Process.Start(new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + script + "\"")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden
                    });
                }
                catch (Exception ex) { Log.Error("uninstall dir", ex); }
            }
        }
    }
}
