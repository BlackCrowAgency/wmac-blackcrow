using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>
    /// Integra WMac en el escritorio de Windows sin modificarlo:
    ///  • localiza SHELLDLL_DefView (en Progman o, si otra app lo movió, en un WorkerW);
    ///  • crea una superficie hija por monitor encima de la lista de iconos nativa;
    ///  • NO envía el mensaje 0x052C ni oculta/mueve ventanas de Explorer;
    ///  • vigila reinicios de explorer.exe, cambios de monitores, DPI y fondo.
    /// Detenerlo destruye las superficies y deja el escritorio exactamente como estaba.
    /// </summary>
    internal sealed class DesktopHost
    {
        readonly DesktopModel _model;
        readonly Launcher _launcher;
        readonly Dispatcher _ui;
        readonly List<DesktopSurface> _surfaces = new List<DesktopSurface>();
        readonly DispatcherTimer _heartbeat, _retry, _check;
        readonly Native.WinEventDelegate _winEventCallback;
        IntPtr _defView, _listView, _hookReorder, _hookFocus;
        uint _explorerPid;
        string _monitorSig, _wallSig;
        int _retries;
        bool _enabled, _forceWallpaper;

        public event Action StateChanged;
        public event Action PrimaryShown;

        /// <summary>El usuario quiere el escritorio WMac activo (aunque se esté reintentando la integración).</summary>
        public bool IsEnabled => _enabled;
        public bool IsAttached => _surfaces.Count > 0;

        public DesktopHost(DesktopModel model, Launcher launcher, Dispatcher ui)
        {
            _model = model;
            _launcher = launcher;
            _ui = ui;
            _winEventCallback = OnWinEvent;
            _heartbeat = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(2) };
            _heartbeat.Tick += (s, e) => Heartbeat();
            _retry = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(1) };
            _retry.Tick += (s, e) => RetryAttach();
            _check = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(600) };
            _check.Tick += (s, e) =>
            {
                _check.Stop();
                if (_forceWallpaper) { _forceWallpaper = false; RefreshWallpaper(); }
                Heartbeat();
            };
        }

        public bool Start()
        {
            if (_enabled && IsAttached) return true;
            _enabled = true;
            Watchdog.Armed = true;
            bool ok = Attach();
            if (!ok) { _retries = 0; _retry.Start(); }
            StateChanged?.Invoke();
            return ok;
        }

        public void Stop(bool animate, Action done = null)
        {
            _enabled = false;
            Watchdog.Armed = false;
            _retry.Stop();
            _heartbeat.Stop();
            _check.Stop();
            RemoveHooks();
            var list = _surfaces.ToList();
            _surfaces.Clear();
            StateChanged?.Invoke();

            if (!animate || list.Count == 0)
            {
                foreach (var s in list) s.Dispose();
                done?.Invoke();
                return;
            }
            int pending = list.Count;
            bool finished = false;
            void Finish()
            {
                if (finished) return;
                finished = true;
                foreach (var s in list) s.Dispose();
                done?.Invoke();
            }
            foreach (var s in list) s.FadeOut(() => { if (--pending == 0) Finish(); });
            // Seguridad: si la animación no termina, cerrar igualmente.
            var guard = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(900) };
            guard.Tick += (s, e) => { guard.Stop(); Finish(); };
            guard.Start();
        }

        public void ScheduleCheck(bool wallpaperChanged)
        {
            if (wallpaperChanged) _forceWallpaper = true;
            _check.Stop();
            _check.Start();
        }

        public void OnExplorerRestarted()
        {
            if (!_enabled) return;
            Log.Write("Explorer reiniciado: volviendo a integrar el escritorio");
            Reattach(1500);
        }

        DesktopSurface Primary => _surfaces.FirstOrDefault(s => s.IsPrimary);

        // ---------- integración ----------

        static IntPtr FindDefView()
        {
            IntPtr progman = Native.FindWindow("Progman", null);
            IntPtr dv = progman != IntPtr.Zero ? Native.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null) : IntPtr.Zero;
            if (dv != IntPtr.Zero) return dv;
            // Algunas apps de fondos animados mueven la vista a un WorkerW: solo se busca, no se cambia nada.
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows((h, l) =>
            {
                if (Native.ClassOf(h) != "WorkerW") return true;
                var d = Native.FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (d == IntPtr.Zero) return true;
                found = d;
                return false;
            }, IntPtr.Zero);
            return found;
        }

        bool Attach()
        {
            try
            {
                _defView = FindDefView();
                if (_defView == IntPtr.Zero) return false;
                Native.GetWindowThreadProcessId(_defView, out _explorerPid);
                _listView = Native.FindWindowEx(_defView, IntPtr.Zero, "SysListView32", null);

                var monitors = Monitors.GetAll();
                if (monitors.Count == 0) return false;
                _monitorSig = Monitors.Signature(monitors);
                var virt = Monitors.VirtualBounds(monitors);
                var walls = WallpaperInfo.Query(monitors);
                _wallSig = WallpaperInfo.QuickSignature();

                for (int i = 0; i < monitors.Count; i++)
                {
                    try
                    {
                        var surface = new DesktopSurface(_defView, monitors[i], i == 0, _model, _launcher, walls[i], virt);
                        if (i == 0) surface.Shown += () => PrimaryShown?.Invoke();
                        _surfaces.Add(surface);
                    }
                    catch (Exception ex) { Log.Error("surface " + monitors[i].Device, ex); }
                }
                if (_surfaces.Count == 0) return false;

                InstallHooks();
                _heartbeat.Start();
                Log.Write($"Integrado en el escritorio: {_surfaces.Count} monitor(es) [{_monitorSig}]");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("attach", ex);
                Detach();
                return false;
            }
        }

        void Detach()
        {
            RemoveHooks();
            _heartbeat.Stop();
            foreach (var s in _surfaces) s.Dispose();
            _surfaces.Clear();
        }

        void Reattach(int delayMs)
        {
            if (!_enabled) return;
            Detach();
            _retries = 0;
            _retry.Stop();
            _retry.Interval = TimeSpan.FromMilliseconds(delayMs);
            _retry.Start();
            StateChanged?.Invoke();
        }

        void RetryAttach()
        {
            _retry.Stop();
            if (!_enabled || IsAttached) return;
            if (Attach()) { StateChanged?.Invoke(); return; }
            // Primero cada segundo; después, cada 10 s mientras siga activado (sin rendirse nunca del todo).
            if (++_retries == 90) Log.Write("El escritorio de Windows sigue sin aparecer; se reintentará cada 10 s.");
            _retry.Interval = TimeSpan.FromSeconds(_retries < 90 ? 1 : 10);
            _retry.Start();
        }

        void Heartbeat()
        {
            if (!_enabled || !IsAttached) return;
            if (!Native.IsWindow(_defView) || _surfaces.Any(s => s.Hwnd == IntPtr.Zero || !Native.IsWindow(s.Hwnd) || Native.GetParent(s.Hwnd) != _defView))
            {
                Log.Write("La vista del escritorio cambió: reintegrando");
                Reattach(1200);
                return;
            }
            EnsureZOrder();
            if (Monitors.Signature(Monitors.GetAll()) != _monitorSig)
            {
                Log.Write("Cambio de monitores/DPI/área de trabajo: reconstruyendo");
                Reattach(250);
                return;
            }
            if (WallpaperInfo.QuickSignature() != _wallSig) RefreshWallpaper();
        }

        void RefreshWallpaper()
        {
            if (!IsAttached) return;
            var monitors = _surfaces.Select(s => s.Monitor).ToList();
            var walls = WallpaperInfo.Query(monitors);
            _wallSig = WallpaperInfo.QuickSignature();
            var virt = Monitors.VirtualBounds(monitors);
            for (int i = 0; i < _surfaces.Count; i++) _surfaces[i].UpdateWallpaper(walls[i], virt);
        }

        /// <summary>Mantiene nuestras superficies por encima de la lista de iconos nativa.</summary>
        void EnsureZOrder()
        {
            if (!IsAttached || !Native.IsWindow(_defView)) return;
            _listView = Native.FindWindowEx(_defView, IntPtr.Zero, "SysListView32", null);
            if (_listView == IntPtr.Zero) return;
            var above = new HashSet<IntPtr>();
            for (var h = Native.GetWindow(_listView, Native.GW_HWNDPREV); h != IntPtr.Zero && above.Count < 64; h = Native.GetWindow(h, Native.GW_HWNDPREV))
                above.Add(h);
            if (_surfaces.All(s => above.Contains(s.Hwnd))) return;
            foreach (var s in _surfaces) s.EnsureTop();
        }

        // ---------- eventos pasivos de Explorer ----------

        void InstallHooks()
        {
            RemoveHooks();
            _hookReorder = Native.SetWinEventHook(Native.EVENT_OBJECT_REORDER, Native.EVENT_OBJECT_REORDER, IntPtr.Zero, _winEventCallback,
                                                  _explorerPid, 0, Native.WINEVENT_OUTOFCONTEXT);
            _hookFocus = Native.SetWinEventHook(Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS, IntPtr.Zero, _winEventCallback,
                                                _explorerPid, 0, Native.WINEVENT_OUTOFCONTEXT);
        }

        void RemoveHooks()
        {
            if (_hookReorder != IntPtr.Zero) { Native.UnhookWinEvent(_hookReorder); _hookReorder = IntPtr.Zero; }
            if (_hookFocus != IntPtr.Zero) { Native.UnhookWinEvent(_hookFocus); _hookFocus = IntPtr.Zero; }
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (!_enabled || hwnd == IntPtr.Zero) return;
            if (evt == Native.EVENT_OBJECT_REORDER && hwnd == _defView)
                _ui.BeginInvoke(DispatcherPriority.Normal, (Action)EnsureZOrder);
            else if (evt == Native.EVENT_OBJECT_FOCUS && (hwnd == _listView || hwnd == _defView))
                // El teclado del escritorio va a WMac: así Supr/Entrar nunca actúan sobre iconos nativos ocultos.
                _ui.BeginInvoke(DispatcherPriority.Input, (Action)(() => Primary?.TakeFocus()));
        }
    }
}
