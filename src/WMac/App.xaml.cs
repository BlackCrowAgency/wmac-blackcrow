using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WMac
{
    public partial class App : Application
    {
        public static App Instance { get; private set; }
        internal DesktopModel Model { get; private set; }
        internal DesktopHost Host { get; private set; }
        internal Launcher Launcher { get; private set; }

        Tray _tray;
        BroadcastWindow _broadcast;
        Mutex _mutex;
        PreviewWindow _preview;
        SettingsWindow _settings;
        MessageDialog _confirm;
        DispatcherTimer _settingChange;
        bool _exiting, _finished, _awaitingConfirm, _wallpaperHint, _activating, _failed;
        DateTime _protectedHint;
        bool _previewOnly;   // iniciado con --preview: al cerrar la vista previa, WMac se cierra
        int _errors;
        DateTime _errorsSince = DateTime.UtcNow;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var args = e.Args.Select(a => a.Trim().ToLowerInvariant()).ToList();
            ThemeManager.Apply();

            if (args.Contains("--restore")) { InstanceControl.RestoreFromCommandLine(); Shutdown(); return; }
            if (args.Contains("--uninstall")) { InstanceControl.Uninstall(); Shutdown(); return; }
            // Respaldo de la distribución del escritorio: guardar y comprobar son de solo lectura; restaurar pide confirmación.
            if (args.Contains("--backup-layout")) { LayoutCommands.Backup(); Shutdown(); return; }
            if (args.Contains("--check-layout")) { LayoutCommands.Check(); Shutdown(); return; }
            int ri = args.IndexOf("--restore-layout");
            if (ri >= 0) { LayoutCommands.Restore(ri + 1 < e.Args.Length ? e.Args[ri + 1] : null); Shutdown(); return; }

            _mutex = new Mutex(true, InstanceControl.MutexName, out bool created);
            if (!created)
            {
                InstanceControl.Signal(args.Contains("--preview") ? "Preview" : "Show");
                _mutex.Dispose();
                _mutex = null;
                Shutdown();
                return;
            }

            Instance = this;
            DispatcherUnhandledException += OnUiException;
            AppDomain.CurrentDomain.UnhandledException += (s, ex) => Log.Write("FATAL " + ex.ExceptionObject);
            // Al cerrar sesión o apagar, Windows retira la capa de WMac: la sesión integrada termina limpiamente.
            SessionEnding += (s, ex) => { SettingsStore.Flush(); LayoutBackups.EndSession(); RunMarker(false); };
            Log.Write("WMac iniciado " + string.Join(" ", args));

            SettingsStore.Load();
            var settings = SettingsStore.Current;
            if (!settings.WelcomeShown) settings.ShowIcons = !NativeIconsHidden();

            Model = new DesktopModel(Dispatcher);
            Launcher = new Launcher(Model);
            var primary = Monitors.GetAll().FirstOrDefault();
            Model.SetIconPx((int)Math.Ceiling(64 * (primary?.Scale ?? 1)));
            Model.Start();
            WeatherService.Shared.Start();
            MediaService.Shared.Start(Dispatcher);

            Host = new DesktopHost(Model, Launcher, Dispatcher);
            Host.StateChanged += () => { _tray?.Update(); _settings?.Refresh(); };
            Host.PrimaryShown += OnPrimaryShown;

            _tray = new Tray();
            _broadcast = new BroadcastWindow(OnBroadcast);
            _settingChange = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _settingChange.Tick += (s, ev) =>
            {
                _settingChange.Stop();
                Host.ScheduleCheck(_wallpaperHint);
                _wallpaperHint = false;
            };
            InstanceControl.Listen(Dispatcher, OnSignal);
            Watchdog.Start(Dispatcher);

            // Si la ejecución anterior no terminó limpiamente (fallo, cierre forzado), el escritorio no se reactiva solo.
            bool crashedBefore = RunMarker(true);
            string unfinished = LayoutBackups.PendingSession();
            bool atLogin = args.Contains("--autostart");
            if (args.Contains("--preview")) { _previewOnly = true; ShowPreview(); }
            else if (unfinished != null) CheckAfterUnexpectedEnd(unfinished);
            else if (crashedBefore && settings.DesktopEnabled)
            {
                SuspendDesktop("la ejecución anterior de WMac no terminó limpiamente");
                _tray.Notify("WMac no se activó", "Se cerró de forma inesperada la última vez. Actívalo desde aquí cuando quieras.");
            }
            // «Mantener» ya confirmado: se muestra sin volver a preguntar (con respaldo previo, como siempre).
            else if (settings.DesktopEnabled && settings.DesktopConfirmed) StartIntegration(false);
            // Al iniciar Windows sin escritorio confirmado, WMac queda en la bandeja: la bienvenida solo la primera vez.
            else if (!atLogin || !settings.WelcomeShown) ShowWelcome();
            else Log.Write("Inicio con Windows: el escritorio WMac no está activado; queda en la bandeja.");
        }

        /// <summary>
        /// Marca «WMac en ejecución» (%LOCALAPPDATA%\WMac\en-ejecucion). <paramref name="on"/>=true la crea y devuelve si ya
        /// existía, es decir, si la ejecución anterior terminó de golpe; false la borra al salir limpiamente.
        /// </summary>
        static bool RunMarker(bool on)
        {
            string path = Path.Combine(Log.DataDir, "en-ejecucion");
            try
            {
                if (!on) { if (File.Exists(path)) File.Delete(path); return false; }
                bool existed = File.Exists(path);
                Directory.CreateDirectory(Log.DataDir);
                File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                return existed;
            }
            catch (Exception ex) { Log.Error("marca de ejecución", ex); return false; }
        }

        /// <summary>El escritorio WMac queda desactivado hasta que el usuario lo vuelva a activar (y a confirmar con «Mantener»).</summary>
        static void SuspendDesktop(string why)
        {
            var s = SettingsStore.Current;
            s.DesktopEnabled = false;
            s.DesktopConfirmed = false;
            SettingsStore.Flush();
            Log.Write("Escritorio WMac desactivado hasta que el usuario lo vuelva a activar: " + why);
        }

        static bool NativeIconsHidden()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                    return k?.GetValue("HideIcons") is int v && v == 1;
            }
            catch { return false; }
        }

        void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Log.Error("UI", e.Exception);
            e.Handled = true;
            if ((DateTime.UtcNow - _errorsSince).TotalSeconds > 30) { _errors = 0; _errorsSince = DateTime.UtcNow; }
            if (++_errors > 8)
            {
                Log.Write("Demasiados errores seguidos: se cierra WMac (el escritorio original queda visible).");
                // Es un fallo: no se reactiva solo, y al volver a abrirse se comprobará el escritorio contra el respaldo.
                _failed = true;
                SuspendDesktop("demasiados errores seguidos");
                ExitApp();
            }
        }

        // ================= flujos principales =================

        void ShowWelcome()
        {
            MessageDialog.Open("Bienvenido a WMac",
                "Un escritorio con widgets, efecto cristal y tus iconos de siempre.\n\n" +
                "Antes de activarse guarda la posición de tus iconos, y al cerrarlo tu escritorio vuelve a estar exactamente como lo dejaste.\n\n" +
                "Si prefieres, pruébalo primero en una ventana.",
                new[] { "Activar escritorio", "Vista previa", "Ahora no" },
                r =>
                {
                    SettingsStore.Current.WelcomeShown = true;
                    SettingsStore.SaveSoon();
                    if (_exiting) return;
                    if (r == "Activar escritorio") ActivateDesktop();
                    else if (r == "Vista previa") ShowPreview();
                    else _tray.Notify("WMac", "Actívalo cuando quieras desde la bandeja.");
                });
        }

        public void ActivateDesktop()
        {
            if (_exiting) return;
            _previewOnly = false;   // el usuario eligió activar: cerrar la vista previa ya no cierra WMac
            _preview?.Close();
            if (Host.IsEnabled) return;
            StartIntegration(true);
        }

        /// <summary>
        /// Antes de cubrir el escritorio, WMac guarda un respaldo de la distribución de los iconos de Windows.
        /// Si no puede leerla o guardarla, NO se integra.
        /// </summary>
        async void StartIntegration(bool interactive)
        {
            if (_activating || Host.IsEnabled) return;
            _activating = true;
            var (snap, error) = interactive ? await DesktopLayout.CaptureAsync("antes de activar") : await CaptureSettled();
            _activating = false;
            if (_exiting || Host.IsEnabled) return;
            string saved = null;
            if (snap != null)
            {
                try { saved = LayoutBackups.Save(snap); }
                catch (Exception ex) { Log.Error("guardar respaldo", ex); error = "No se pudo escribir el respaldo: " + ex.Message; }
            }
            if (saved == null)
            {
                const string title = "WMac no se activó";
                string body = "No se pudo guardar la posición de tus iconos, así que WMac no se activó. Tu escritorio sigue igual.\n\n" + error;
                if (interactive) MessageDialog.Info(title, body);
                else _tray.Notify(title, "No se pudo guardar la posición de tus iconos. Tu escritorio sigue igual.");
                _settings?.Refresh();
                return;
            }
            LayoutBackups.BeginSession(saved);

            var s = SettingsStore.Current;
            _awaitingConfirm = interactive && !s.DesktopConfirmed;
            if (!_awaitingConfirm) { s.DesktopEnabled = true; SettingsStore.SaveSoon(); }
            if (!Host.Start() && interactive)
            {
                MessageDialog.Info("Esperando al escritorio", "Windows aún no muestra el escritorio. WMac lo intentará de nuevo en unos segundos.");
            }
        }

        /// <summary>
        /// Al iniciar Windows el escritorio tarda unos segundos en aparecer y en colocar sus iconos. El respaldo se toma cuando
        /// dos lecturas seguidas coinciden (mismos iconos, mismas posiciones); se espera como mucho 90 s.
        /// </summary>
        async Task<(LayoutSnapshot Snap, string Error)> CaptureSettled()
        {
            LayoutSnapshot prev = null;
            string error = null;
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until && !_exiting)
            {
                var (s, e) = await DesktopLayout.CaptureAsync("antes de activar (inicio de Windows)");
                if (s == null) { error = e; prev = null; }
                else if (prev != null && prev.Icons.Count == s.Icons.Count && DesktopLayout.Compare(prev, s).LayoutIntact) return (s, null);
                else prev = s;
                await Task.Delay(s == null ? 3000 : 2500);
            }
            return (null, error ?? "El escritorio de Windows tardó demasiado en cargarse.");
        }

        void OnPrimaryShown()
        {
            if (!_awaitingConfirm || _exiting) return;
            _awaitingConfirm = false;
            var s = SettingsStore.Current;
            _confirm = MessageDialog.Open("¿Se ve bien tu nuevo escritorio?",
                "Pulsa «Mantener» y WMac se abrirá así cada vez que inicies Windows.\n\n" +
                "Si algo no va bien, pulsa «Restaurar» o espera: volverás a tu escritorio de siempre.",
                new[] { "Mantener", "Restaurar" },
                r =>
                {
                    _confirm = null;
                    if (_exiting) return;
                    if (r == "Mantener")
                    {
                        s.DesktopEnabled = true;
                        s.DesktopConfirmed = true;
                        SettingsStore.SaveSoon();
                        Autostart.Set(true);   // «Mantener» = mostrarlo también al iniciar Windows
                        _tray.Update();
                        _settings?.Refresh();
                        _tray.Notify("Listo", "WMac se abrirá con Windows. Puedes volver a tu escritorio original desde la bandeja.");
                    }
                    else DeactivateDesktop();
                },
                countdownSeconds: 15, countdownFormat: "Se restaurará en {0} s.", timeoutResult: "Restaurar");
        }

        public void DeactivateDesktop()
        {
            _awaitingConfirm = false;
            bool wasOn = Host.IsEnabled;
            Host.Stop(true);
            SettingsStore.Current.DesktopEnabled = false;
            SettingsStore.SaveSoon();
            if (wasOn) VerifyAfterSession("al desactivar", interactive: true);
        }

        // ================= respaldo y comprobación del escritorio original =================

        /// <summary>
        /// Al terminar una sesión integrada se compara el escritorio con el respaldo tomado al activarla (solo lectura).
        /// Si algo no coincide, se ofrece devolver cada icono a su sitio; nunca se hace sin preguntar.
        /// </summary>
        async void VerifyAfterSession(string when, bool interactive, Action then = null)
        {
            var baseline = LayoutBackups.Load(LayoutBackups.PendingSession()) ?? LayoutBackups.Latest();
            LayoutBackups.EndSession();
            if (baseline == null) { then?.Invoke(); return; }
            var (now, error) = await DesktopLayout.CaptureAsync("comprobación " + when);
            if (now == null)
            {
                Log.Write($"No se pudo comprobar el escritorio {when}: {error}");
                if (interactive) MessageDialog.Open("WMac se cerró", "No se pudo comparar tu escritorio con el respaldo ahora mismo.",
                                                    new[] { "Aceptar" }, _ => then?.Invoke());
                else then?.Invoke();
                return;
            }
            var diff = DesktopLayout.Compare(baseline, now);
            Log.Write($"Comprobación {when} frente al respaldo del {baseline.Taken}: {diff.Describe().Replace("\n", " ")}");
            if (diff.LayoutIntact)
            {
                // Si WMac va a cerrarse después (Restaurar escritorio original), el aviso de la bandeja desaparecería con él: diálogo.
                if (interactive && then != null)
                {
                    MessageDialog.Open("Todo en su sitio", diff.Describe(),
                        new[] { "Aceptar" }, _ => then());
                    return;
                }
                if (interactive) _tray?.Notify("Todo en su sitio", "Tus iconos están donde estaban.");
                then?.Invoke();
                return;
            }
            if (!interactive) { then?.Invoke(); return; }
            OfferRestore(baseline, diff, null, then);
        }

        void CheckAfterUnexpectedEnd(string backupPath)
        {
            Log.Write("La sesión anterior de WMac no terminó limpiamente: comprobando el escritorio");
            var baseline = LayoutBackups.Load(backupPath) ?? LayoutBackups.Latest();
            LayoutBackups.EndSession();
            SuspendDesktop("la sesión integrada anterior terminó de golpe");   // queda desactivado hasta que el usuario decida
            _ = CheckAgainst(baseline, "WMac se cerró de forma inesperada",
                "La última vez WMac se cerró de forma inesperada, así que no se ha vuelto a activar.", offerActivate: true);
        }

        /// <summary>Comprobar ahora (bandeja o Ajustes): compara con el respaldo más reciente.</summary>
        public void CheckDesktopNow() => _ = CheckAgainst(LayoutBackups.Latest(), "Comprobar mi escritorio", null, offerActivate: false);

        async Task CheckAgainst(LayoutSnapshot baseline, string title, string intro, bool offerActivate)
        {
            string lead = intro != null ? intro + "\n\n" : "";
            if (baseline == null)
            {
                MessageDialog.Info(title, lead + "Aún no hay respaldo. Se guarda uno automáticamente al activar WMac.");
                return;
            }
            var (now, error) = await DesktopLayout.CaptureAsync("comprobación");
            if (now == null)
            {
                MessageDialog.Info(title, lead + "No se pudo comparar tu escritorio ahora mismo.");
                return;
            }
            var diff = DesktopLayout.Compare(baseline, now);
            Log.Write($"Comprobación frente al respaldo del {baseline.Taken}: {diff.Describe().Replace("\n", " ")}");
            if (!diff.LayoutIntact) { OfferRestore(baseline, diff, intro, null); return; }
            if (offerActivate)
                MessageDialog.Open(title, lead + diff.Describe(), new[] { "Activar de nuevo", "Ahora no" }, r => { if (r == "Activar de nuevo") ActivateDesktop(); });
            else MessageDialog.Info(title, lead + diff.Describe());
        }

        void OfferRestore(LayoutSnapshot baseline, LayoutDiff diff, string intro, Action then)
        {
            MessageDialog.Open("Algunos iconos cambiaron",
                (intro != null ? intro + "\n\n" : "") + diff.Describe() +
                "\n\nWMac no mueve tus iconos: lo hizo Windows o se movieron a mano. ¿Quieres devolverlos a su sitio? Tus archivos no se tocan.",
                new[] { "Devolver a su sitio", "Dejar así" },
                async r =>
                {
                    if (r != "Devolver a su sitio") { then?.Invoke(); return; }
                    var res = await DesktopLayout.RestoreAsync(baseline);
                    MessageDialog.Open(res.Error == null ? "Listo" : "No se pudo restaurar",
                        res.Error == null ? res.Describe() : "No se pudo terminar de restaurar. Inténtalo de nuevo más tarde.",
                        new[] { "Aceptar" }, _ => then?.Invoke());
                });
        }

        public async void BackupNow()
        {
            var (snap, error) = await DesktopLayout.CaptureAsync("manual");
            if (snap == null) { MessageDialog.Info("No se guardó el respaldo", "No se pudo leer tu escritorio ahora mismo."); return; }
            try
            {
                LayoutBackups.Save(snap);
                MessageDialog.Info("Respaldo guardado", $"Se guardó la posición de tus {snap.Icons.Count} iconos.");
                _settings?.Refresh();
            }
            catch (Exception ex) { Log.Error("guardar respaldo", ex); MessageDialog.Info("No se guardó el respaldo", ex.Message); }
        }

        /// <summary>Aviso (como mucho uno cada 30 s) cuando la protección de archivos bloquea una acción.</summary>
        public void NotifyProtected()
        {
            if ((DateTime.UtcNow - _protectedHint).TotalSeconds < 30) return;
            _protectedHint = DateTime.UtcNow;
            _tray?.Notify("Archivos protegidos", "Para eliminar, renombrar o mover archivos, desactiva la protección en Ajustes.");
        }

        public void ToggleDesktop()
        {
            if (Host.IsEnabled) DeactivateDesktop();
            else ActivateDesktop();
        }

        public void ShowPreview()
        {
            if (_exiting) return;
            if (_preview == null)
            {
                _preview = new PreviewWindow(Model, Launcher);
                _preview.Closed += (s, e) =>
                {
                    _preview = null;
                    if (_previewOnly && !Host.IsEnabled) ExitApp();
                };
                _preview.Show();
            }
            _preview.Activate();
        }

        public void ShowSettings()
        {
            if (_exiting) return;
            if (_settings == null)
            {
                _settings = new SettingsWindow();
                _settings.Closed += (s, e) => _settings = null;
                _settings.Show();
            }
            else _settings.Refresh();
            if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
            _settings.Activate();
        }

        public void SetAutostart(bool on)
        {
            Autostart.Set(on);
            _tray?.Update();
            _settings?.Refresh();
        }

        /// <summary>Cierra WMac, desactiva el inicio automático y deja el escritorio original.</summary>
        /// <param name="interactive">false cuando lo pide «Restaurar escritorio original» del menú Inicio: ese proceso hace la comprobación.</param>
        public void RestoreOriginal(bool interactive = true)
        {
            Autostart.Set(false);
            SuspendDesktop("el usuario eligió «Restaurar escritorio original»");
            bool wasOn = Host.IsEnabled || LayoutBackups.PendingSession() != null;
            _awaitingConfirm = false;
            Host.Stop(true);
            if (wasOn && interactive) VerifyAfterSession("al restaurar", interactive: true, then: ExitApp);
            else ExitApp();
        }

        public void ExitApp()
        {
            if (_exiting) return;
            _exiting = true;
            try { _confirm?.Close(); _settings?.Close(); _preview?.Close(); } catch { }
            Host.Stop(true, () => Dispatcher.BeginInvoke((Action)FinishExit));
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            guard.Tick += (s, e) => { guard.Stop(); FinishExit(); };
            guard.Start();
        }

        void FinishExit()
        {
            if (_finished) return;
            _finished = true;
            try { Model?.Dispose(); } catch { }
            WeatherService.Shared.Stop();
            MediaService.Shared.Stop();
            _tray?.Dispose();
            _broadcast?.Dispose();
            SettingsStore.Flush();
            if (!_failed)
            {
                LayoutBackups.EndSession();   // salida limpia: la capa ya se retiró
                RunMarker(false);
            }
            try { _mutex?.ReleaseMutex(); } catch { }
            _mutex?.Dispose();
            Log.Write("WMac cerrado");
            Shutdown();
        }

        // ================= mensajes del sistema / otras instancias =================

        void OnSignal(string name)
        {
            switch (name)
            {
                case "Show": ShowSettings(); break;
                case "Preview": ShowPreview(); break;
                case "Exit": ExitApp(); break;
                case "Restore": RestoreOriginal(interactive: false); break;
            }
        }

        void OnBroadcast(int msg, IntPtr wParam, IntPtr lParam)
        {
            if ((uint)msg == _broadcast.TaskbarCreated)
            {
                Host.OnExplorerRestarted();
                return;
            }
            switch (msg)
            {
                case Native.WM_SETTINGCHANGE:
                    string area = lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null;
                    if (wParam.ToInt64() == Native.SPI_SETDESKWALLPAPER) _wallpaperHint = true;
                    if (area == "ImmersiveColorSet") ThemeManager.Apply();
                    if (area == null || area == "ShellState") Model.ScheduleRescan();
                    _settingChange.Stop();
                    _settingChange.Start();
                    break;
                case Native.WM_DISPLAYCHANGE:
                    _settingChange.Stop();
                    _settingChange.Start();
                    break;
                case Native.WM_TIMECHANGE:
                    Clockwork.RaiseTimeChanged();
                    break;
                case Native.WM_POWERBROADCAST:
                    long ev = wParam.ToInt64();
                    if (ev == Native.PBT_APMRESUMEAUTOMATIC || ev == Native.PBT_APMRESUMESUSPEND)
                    {
                        Clockwork.RaiseTimeChanged();
                        Host.ScheduleCheck(true);
                        WeatherService.Shared.Refresh();
                    }
                    break;
                case Native.WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) { SettingsStore.Flush(); LayoutBackups.EndSession(); RunMarker(false); }
                    break;
            }
        }
    }
}
