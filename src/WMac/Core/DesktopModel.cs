using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WMac
{
    public sealed class DesktopItem : INotifyPropertyChanged
    {
        string _name;
        ImageSource _icon;

        public string Key;           // ruta en minúsculas o ::{guid}
        public string Path;          // ruta real o nombre de análisis del Shell
        public string TypeName = "";
        public string Target;        // destino del acceso directo (.lnk) o URL (.url)
        internal Category Category;  // clasificación automática (solo visual)
        public bool IsFolder, IsSpecial, IsShortcut, AllowThumbnail;
        public DateTime Modified;
        public long Size;
        public int Col = -1, Row = -1;

        public string Name { get => _name; set { if (_name != value) { _name = value; Raise(nameof(Name)); } } }
        public ImageSource Icon { get => _icon; set { _icon = value; Raise(nameof(Icon)); } }

        public bool IsRecycleBin => Key == DesktopModel.RecycleBinKey;
        public bool IsFileSystem => !IsSpecial;
        public string Extension => IsFolder || IsSpecial ? "" : System.IO.Path.GetExtension(Path).ToLowerInvariant();

        public event PropertyChangedEventHandler PropertyChanged;
        void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }

    /// <summary>
    /// Elementos reales del escritorio (usuario + público + iconos de sistema visibles).
    /// Solo lectura: nunca crea, mueve ni borra nada por sí mismo.
    /// </summary>
    internal sealed class DesktopModel : IDisposable
    {
        public const string RecycleBinKey = "::{645ff040-5081-101b-9f08-00aa002f954e}";

        static readonly (string Guid, string Parse, bool DefaultShown)[] Specials =
        {
            ("{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", false), // Este equipo
            ("{59031a47-3f72-44a7-89c5-5595fe6b30ee}", "::{59031a47-3f72-44a7-89c5-5595fe6b30ee}", false), // Carpeta del usuario
            ("{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", false), // Red
            ("{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", "::{26EE0668-A00A-44D7-9371-BEB064C98683}", false), // Panel de control
            ("{645FF040-5081-101B-9F08-00AA002F954E}", "::{645FF040-5081-101B-9F08-00AA002F954E}", true),  // Papelera de reciclaje
        };

        static readonly HashSet<string> ThumbExt = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".heic", ".tif", ".tiff" };

        readonly Dispatcher _ui;
        readonly StaWorker _worker = new StaWorker("WMac.Shell");
        readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        readonly DispatcherTimer _rescanTimer, _binTimer;
        readonly Dictionary<string, (int c, int r, DateTime at)> _pendingPlacement = new Dictionary<string, (int, int, DateTime)>(StringComparer.OrdinalIgnoreCase);
        string _pendingRename;
        bool _binEmpty = true, _firstApply = true, _disposed;

        public readonly List<DesktopItem> Items = new List<DesktopItem>();
        public string UserDesktop { get; } = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);
        public string PublicDesktop { get; } = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolderOption.DoNotVerify);

        /// <summary>Solo se crean/copian archivos si la carpeta Escritorio existe y es una ruta absoluta.</summary>
        public bool CanWriteDesktop => ShellOps.IsUsableDir(UserDesktop);
        public int IconPx { get; private set; } = 64;

        public event Action ItemsChanged;
        public event Action<DesktopItem> RenameRequested;

        public DesktopModel(Dispatcher ui)
        {
            _ui = ui;
            _rescanTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(350) };
            _rescanTimer.Tick += (s, e) => { _rescanTimer.Stop(); Rescan(); };
            _binTimer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromSeconds(6) };
            _binTimer.Tick += (s, e) => CheckRecycleBin();
        }

        public void Start(bool watch = true)
        {
            Rescan();
            if (!watch) return;
            foreach (var dir in new[] { UserDesktop, PublicDesktop }.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var w = new FileSystemWatcher(dir)
                    {
                        IncludeSubdirectories = false,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Attributes,
                        InternalBufferSize = 32 * 1024
                    };
                    w.Created += (s, e) => ScheduleRescanFromAnyThread();
                    w.Deleted += (s, e) => ScheduleRescanFromAnyThread();
                    w.Changed += (s, e) => ScheduleRescanFromAnyThread();
                    w.Renamed += (s, e) => _ui.BeginInvoke((Action)(() => OnRenamed(e.OldFullPath, e.FullPath)));
                    w.Error += (s, e) => ScheduleRescanFromAnyThread();
                    w.EnableRaisingEvents = true;
                    _watchers.Add(w);
                }
                catch (Exception ex) { Log.Error("watcher " + dir, ex); }
            }
            _binTimer.Start();
        }

        void ScheduleRescanFromAnyThread() => _ui.BeginInvoke((Action)ScheduleRescan);

        public void ScheduleRescan()
        {
            if (_disposed) return;
            _rescanTimer.Stop();
            _rescanTimer.Start();
        }

        void OnRenamed(string oldPath, string newPath)
        {
            var old = Items.FirstOrDefault(i => string.Equals(i.Path, oldPath, StringComparison.OrdinalIgnoreCase));
            if (old != null && old.Col >= 0) _pendingPlacement[newPath] = (old.Col, old.Row, DateTime.UtcNow);
            ScheduleRescan();
        }

        public void Rescan()
        {
            if (_disposed) return;
            _worker.Post(() =>
            {
                var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var fresh = ScanCore(scanned);
                _ui.BeginInvoke((Action)(() => Apply(fresh, scanned)));
            });
        }

        /// <summary>Recuerda dónde debe aparecer un archivo que todavía no existe (pegar, soltar, nueva carpeta).</summary>
        public void SetPendingPlacement(string fullPath, int col, int row) => _pendingPlacement[fullPath] = (col, row, DateTime.UtcNow);

        public void RequestRenameWhenAppears(string fullPath) => _pendingRename = fullPath;

        public void SetIconPx(int px)
        {
            px = Math.Max(16, Math.Min(256, px));
            // Reducir un poco no merece recargar: el icono actual se escala con calidad alta.
            if (px == IconPx || (px < IconPx && px >= IconPx * 0.6)) return;
            IconPx = px;
            foreach (var it in Items) LoadIcon(it);
        }

        public void ReloadIcons()
        {
            foreach (var it in Items) LoadIcon(it);
        }

        public void SavePosition(DesktopItem it)
        {
            if (it.Col < 0 || it.Row < 0) return;
            SettingsStore.Current.Layout[it.Key] = $"{it.Col},{it.Row}";
            SettingsStore.SaveSoon();
        }

        // ---------- escaneo (hilo del Shell) ----------

        List<DesktopItem> ScanCore(HashSet<string> scannedDirs)
        {
            var list = new List<DesktopItem>();
            bool showHidden = false, showSuper = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced"))
                {
                    showHidden = k?.GetValue("Hidden") is int h && h == 1;
                    showSuper = k?.GetValue("ShowSuperHidden") is int sh && sh == 1;
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel"))
                {
                    foreach (var sp in Specials)
                    {
                        bool shown = k?.GetValue(sp.Guid) is int v ? v == 0 : sp.DefaultShown;
                        if (!shown) continue;
                        string name = ShellIcons.GetDisplayName(sp.Parse);
                        if (string.IsNullOrEmpty(name)) continue;
                        list.Add(new DesktopItem
                        {
                            Key = "::" + sp.Guid.ToLowerInvariant(),
                            Path = sp.Parse,
                            Name = name,
                            IsSpecial = true,
                            IsFolder = true,
                            TypeName = "",
                            Category = Category.Utilidades
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Error("scan specials", ex); }

            foreach (var dir in new[] { UserDesktop, PublicDesktop }.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                IEnumerable<string> entries;
                try
                {
                    if (!Path.IsPathRooted(dir) || !Directory.Exists(dir)) continue;
                    entries = Directory.EnumerateFileSystemEntries(dir).ToList();
                    scannedDirs.Add(dir.TrimEnd('\\'));
                }
                catch (Exception ex) { Log.Error("scan " + dir, ex); continue; }

                foreach (var path in entries)
                {
                    try
                    {
                        string fileName = System.IO.Path.GetFileName(path);
                        if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || fileName.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) continue;
                        var attr = File.GetAttributes(path);
                        bool hidden = (attr & FileAttributes.Hidden) != 0, system = (attr & FileAttributes.System) != 0;
                        if (hidden && system && !showSuper) continue;
                        if (hidden && !showHidden) continue;
                        bool isDir = (attr & FileAttributes.Directory) != 0;

                        var sfi = new Native.SHFILEINFO();
                        Native.SHGetFileInfo(path, 0, ref sfi, (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_DISPLAYNAME | Native.SHGFI_TYPENAME);
                        string ext = isDir ? "" : System.IO.Path.GetExtension(path);
                        var it = new DesktopItem
                        {
                            Key = path.ToLowerInvariant(),
                            Path = path,
                            Name = string.IsNullOrEmpty(sfi.szDisplayName) ? fileName : sfi.szDisplayName,
                            TypeName = sfi.szTypeName ?? "",
                            IsFolder = isDir,
                            IsShortcut = ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase),
                            AllowThumbnail = !isDir && ThumbExt.Contains(ext) && (attr & (FileAttributes)0x00440000) == 0 && (attr & FileAttributes.Offline) == 0
                        };
                        try
                        {
                            if (isDir) it.Modified = Directory.GetLastWriteTime(path);
                            else { var fi = new FileInfo(path); it.Modified = fi.LastWriteTime; it.Size = fi.Length; }
                        }
                        catch { }
                        it.Target = ReadTarget(path, ext);
                        it.Category = Categorizer.Classify(it);
                        list.Add(it);
                    }
                    catch (Exception ex) { Log.Error("scan item", ex); }
                }
            }
            return list;
        }

        /// <summary>Destino de un acceso directo, solo lectura (para clasificarlo).</summary>
        static string ReadTarget(string path, string ext)
        {
            try
            {
                if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return ShellOps.ReadShortcutTarget(path);
                if (ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
                {
                    var fi = new FileInfo(path);
                    if (fi.Length > 64 * 1024) return null;
                    foreach (var line in File.ReadLines(path))
                        if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) return line.Substring(4).Trim();
                }
            }
            catch { }
            return null;
        }

        // ---------- aplicar cambios (hilo de la interfaz) ----------

        void Apply(List<DesktopItem> fresh, HashSet<string> scannedDirs)
        {
            if (_disposed) return;
            var layout = SettingsStore.Current.Layout;
            // Las ubicaciones pendientes caducan: así un nombre "esperado" que nunca llegó no se reutiliza más tarde.
            foreach (var k in _pendingPlacement.Where(kv => (DateTime.UtcNow - kv.Value.at).TotalMinutes > 2).Select(kv => kv.Key).ToList())
                _pendingPlacement.Remove(k);
            var byKey = Items.ToDictionary(i => i.Key);
            var next = new List<DesktopItem>();
            bool structural = false;
            DesktopItem toRename = null;

            foreach (var f in fresh)
            {
                if (byKey.TryGetValue(f.Key, out var existing))
                {
                    existing.Name = f.Name;
                    existing.TypeName = f.TypeName;
                    existing.Modified = f.Modified;
                    existing.Size = f.Size;
                    existing.IsFolder = f.IsFolder;
                    existing.Target = f.Target;
                    existing.Category = f.Category;
                    next.Add(existing);
                    byKey.Remove(f.Key);
                    continue;
                }

                structural = true;
                if (_pendingPlacement.TryGetValue(f.Path, out var pp))
                {
                    f.Col = pp.c; f.Row = pp.r;
                    _pendingPlacement.Remove(f.Path);
                    layout[f.Key] = $"{f.Col},{f.Row}";
                    SettingsStore.SaveSoon();
                }
                else if (layout.TryGetValue(f.Key, out var cell))
                {
                    var parts = cell.Split(',');
                    if (parts.Length == 2 && int.TryParse(parts[0], out int c) && int.TryParse(parts[1], out int r)) { f.Col = c; f.Row = r; }
                }
                if (_pendingRename != null && string.Equals(_pendingRename, f.Path, StringComparison.OrdinalIgnoreCase))
                {
                    toRename = f;
                    _pendingRename = null;
                }
                next.Add(f);
                LoadIcon(f);
            }
            if (byKey.Count > 0) structural = true;

            Items.Clear();
            Items.AddRange(next);

            if (_firstApply)
            {
                _firstApply = false;
                // Solo se olvidan posiciones de carpetas que se pudieron leer (una carpeta ausente no borra su disposición).
                var present = new HashSet<string>(Items.Select(i => i.Key), StringComparer.OrdinalIgnoreCase);
                var stale = layout.Keys.Where(k => !k.StartsWith("::") && !present.Contains(k) &&
                                                   scannedDirs.Contains((Path.GetDirectoryName(k) ?? "").TrimEnd('\\'))).ToList();
                foreach (var k in stale) layout.Remove(k);
                if (stale.Count > 0) SettingsStore.SaveSoon();
                structural = true;
            }

            if (structural) ItemsChanged?.Invoke();
            if (toRename != null) RenameRequested?.Invoke(toRename);
        }

        void LoadIcon(DesktopItem it)
        {
            int px = IconPx;
            string path = it.Path;
            bool thumb = it.AllowThumbnail;
            _worker.Post(() =>
            {
                var bmp = ShellIcons.Get(path, px, thumb);
                if (bmp != null) _ui.BeginInvoke((Action)(() => { if (px == IconPx) it.Icon = bmp; }));
            });
        }

        void CheckRecycleBin()
        {
            var bin = Items.FirstOrDefault(i => i.IsRecycleBin);
            if (bin == null) return;
            _worker.Post(() =>
            {
                bool empty = ShellIcons.IsRecycleBinEmpty();
                _ui.BeginInvoke((Action)(() =>
                {
                    if (empty == _binEmpty) return;
                    _binEmpty = empty;
                    LoadIcon(bin);
                }));
            });
        }

        public void Dispose()
        {
            _disposed = true;
            _rescanTimer.Stop();
            _binTimer.Stop();
            foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
            _watchers.Clear();
            _worker.Dispose();
        }
    }
}
