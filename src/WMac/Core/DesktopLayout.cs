using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace WMac
{
    /// <summary>
    /// Respaldo de la distribución ORIGINAL del escritorio de Windows (la de Explorer, no la de WMac):
    /// posición de cada icono, modo de vista, orden, «Organizar automáticamente» / «Alinear a la cuadrícula»,
    /// los valores con los que Explorer guarda todo eso en el registro y un inventario de los archivos del Escritorio.
    ///
    /// Capturar y comparar son de SOLO LECTURA. Lo único que escribe en el escritorio es <see cref="Restore"/>,
    /// y solo se llama cuando el usuario lo confirma explícitamente.
    /// </summary>
    internal static class DesktopLayout
    {
        public const string BagsKey = @"Software\Microsoft\Windows\Shell\Bags\1\Desktop";
        const uint FWF_AUTOARRANGE = 0x1, FWF_SNAPTOGRID = 0x4;
        const uint ArrangeMask = FWF_AUTOARRANGE | FWF_SNAPTOGRID;

        // ---------- captura (solo lectura) ----------

        public static LayoutSnapshot Capture(string reason)
        {
            var snap = RunSta(() => { using (var v = ShellView.OpenDesktop()) return ReadView(v); }, 15000);
            snap.Reason = reason;
            snap.Taken = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            snap.Screen = Monitors.Signature(Monitors.GetAll());
            snap.Registry = ReadBags();
            snap.Files = ReadFiles();
            if (snap.Icons.Count == 0 && snap.Files.Count > 0)
                throw new InvalidOperationException("No se pudo leer la posición de tus iconos. " +
                    "Si los tienes ocultos en Windows, muéstralos (clic derecho en el escritorio › Ver › Mostrar iconos del escritorio).");
            return snap;
        }

        public static LayoutSnapshot TryCapture(string reason, out string error)
        {
            try { error = null; return Capture(reason); }
            catch (Exception ex)
            {
                Log.Error("captura del escritorio", ex);
                error = ex.Message;
                return null;
            }
        }

        /// <summary>Captura sin bloquear la interfaz. Devuelve el respaldo o el motivo por el que no se pudo leer.</summary>
        public static Task<(LayoutSnapshot Snap, string Error)> CaptureAsync(string reason) =>
            Task.Run(() => { var s = TryCapture(reason, out string e); return (s, e); });

        public static Task<RestoreResult> RestoreAsync(LayoutSnapshot s) =>
            Task.Run(() =>
            {
                try { return Restore(s); }
                catch (Exception ex) { Log.Error("restaurar distribución", ex); return new RestoreResult { Error = ex.Message }; }
            });

        /// <summary>
        /// Lee cualquier vista de carpeta del Shell a partir de su navegador (IServiceProvider). En producción es el escritorio;
        /// las pruebas aisladas usan un ExplorerBrowser propio sobre una carpeta temporal.
        /// </summary>
        internal static LayoutSnapshot CaptureView(object browserSite)
        {
            using (var v = ShellView.FromSite(browserSite)) return ReadView(v);
        }

        internal static RestoreResult RestoreView(object browserSite, LayoutSnapshot s)
        {
            using (var v = ShellView.FromSite(browserSite)) return RestoreInto(v, s);
        }

        static LayoutSnapshot ReadView(ShellView view)
        {
            var snap = new LayoutSnapshot();
            var fv = view.FolderView;
            Check(fv.GetCurrentViewMode(out uint mode), "GetCurrentViewMode");
            snap.ViewMode = (int)mode;
            var sp = new Native.POINT();
            if (fv.GetSpacing(ref sp) == 0) { snap.SpacingX = sp.X; snap.SpacingY = sp.Y; }
            snap.AutoArrange = fv.GetAutoArrange() == 0;
            if (view.FolderView2 != null)
            {
                if (view.FolderView2.GetCurrentFolderFlags(out uint flags) == 0) { snap.FolderFlags = flags; snap.FlagsKnown = true; }
                if (view.FolderView2.GetViewModeAndIconSize(out uint m, out int size) == 0) { snap.ViewMode = (int)m; snap.IconSize = size; }
                snap.Sort = ReadSort(view.FolderView2);
            }
            foreach (var it in view.Items())
            {
                if (fv.GetItemPosition(it.Pidl, out var pt) != 0) continue;
                snap.Icons.Add(new IconPos { Id = it.Id, Name = it.Name, X = pt.X, Y = pt.Y });
            }
            return snap;
        }

        static List<string> ReadSort(IFolderView2 fv2)
        {
            var list = new List<string>();
            if (fv2.GetSortColumnCount(out int n) != 0 || n <= 0) return list;
            var cols = new SORTCOLUMN[n];
            if (fv2.GetSortColumns(cols, n) != 0) return list;
            foreach (var c in cols) list.Add($"{c.fmtid:D}|{c.pid}|{c.direction}");
            return list;
        }

        /// <summary>Valores con los que Explorer guarda la distribución (incluye IconLayouts). Solo lectura.</summary>
        static Dictionary<string, string> ReadBags()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(BagsKey, false))
                {
                    if (k == null) return d;
                    foreach (var name in k.GetValueNames())
                    {
                        var kind = k.GetValueKind(name);
                        object v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        d[name] = RegValue.Encode(kind, v);
                    }
                }
            }
            catch (Exception ex) { Log.Error("leer Bags", ex); }
            return d;
        }

        static List<FileEntry> ReadFiles()
        {
            var list = new List<FileEntry>();
            var dirs = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolderOption.DoNotVerify),
            };
            foreach (var dir in dirs.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    foreach (var p in Directory.EnumerateFileSystemEntries(dir))
                    {
                        try
                        {
                            var fi = new FileInfo(p);
                            bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                            list.Add(new FileEntry { Path = p, Dir = isDir, Size = isDir ? 0 : fi.Length, Modified = fi.LastWriteTimeUtc.Ticks });
                        }
                        catch { list.Add(new FileEntry { Path = p }); }
                    }
                }
                catch (Exception ex) { Log.Error("inventario " + dir, ex); }
            }
            return list;
        }

        // ---------- comparación (solo lectura) ----------

        public static LayoutDiff Compare(LayoutSnapshot was, LayoutSnapshot now)
        {
            var diff = new LayoutDiff();
            var before = was.Icons.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var after = now.Icons.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var b in before.Values)
            {
                if (!after.TryGetValue(b.Id, out var a)) { diff.Missing.Add(b.Name); continue; }
                if (Math.Abs(a.X - b.X) > 2 || Math.Abs(a.Y - b.Y) > 2) diff.Moved.Add(b.Name);
            }
            foreach (var a in after.Values) if (!before.ContainsKey(a.Id)) diff.Added.Add(a.Name);

            diff.ViewChanged = (was.FlagsKnown && now.FlagsKnown && (was.FolderFlags & ArrangeMask) != (now.FolderFlags & ArrangeMask)) || was.AutoArrange != now.AutoArrange
                               || was.ViewMode != now.ViewMode || (was.IconSize != 0 && now.IconSize != 0 && was.IconSize != now.IconSize)
                               || !was.Sort.SequenceEqual(now.Sort);
            diff.ScreenChanged = !string.Equals(was.Screen, now.Screen, StringComparison.Ordinal);

            var fb = was.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var fa = now.Files.GroupBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            foreach (var f in fb.Values)
            {
                if (!fa.TryGetValue(f.Path, out var g)) diff.FilesMissing.Add(Path.GetFileName(f.Path));
                else if (!f.Dir && (f.Size != g.Size || f.Modified != g.Modified)) diff.FilesChanged.Add(Path.GetFileName(f.Path));
            }
            foreach (var f in fa.Values) if (!fb.ContainsKey(f.Path)) diff.FilesAdded.Add(Path.GetFileName(f.Path));
            return diff;
        }

        // ---------- restauración (ESCRIBE: solo con confirmación del usuario) ----------

        /// <summary>
        /// Devuelve los iconos a las posiciones del respaldo usando la propia vista de Explorer
        /// (IFolderView::SelectAndPositionItems). No crea, mueve ni borra archivos: si un archivo del respaldo
        /// ya no existe, solo se informa.
        /// </summary>
        public static RestoreResult Restore(LayoutSnapshot s)
        {
            var r = RunSta(() => { using (var v = ShellView.OpenDesktop()) return RestoreInto(v, s); }, 30000);
            Log.Write($"Distribución restaurada desde {s.Taken}: {r.Moved} recolocados, {r.AlreadyInPlace} ya en su sitio, {r.Missing} ausentes, {r.Failed} fallidos");
            return r;
        }

        static RestoreResult RestoreInto(ShellView view, LayoutSnapshot s)
        {
            var r = new RestoreResult();
            var fv = view.FolderView;
            var fv2 = view.FolderView2;
            if (fv2 != null && s.IconSize > 0 && fv2.GetViewModeAndIconSize(out uint m, out int size) == 0 && (m != (uint)s.ViewMode || size != s.IconSize))
                r.ViewRestored |= fv2.SetViewModeAndIconSize((uint)s.ViewMode, s.IconSize) == 0;

            // Las posiciones solo se respetan sin «Organizar automáticamente»: se desactiva mientras se recolocan
            // y al final se deja exactamente como estaba en el respaldo.
            uint current = 0;
            bool flags = fv2 != null && s.FlagsKnown && fv2.GetCurrentFolderFlags(out current) == 0;
            if (flags)
            {
                r.ViewRestored |= (current & ArrangeMask) != (s.FolderFlags & ArrangeMask);
                fv2.SetCurrentFolderFlags(ArrangeMask, s.FolderFlags & FWF_SNAPTOGRID);
            }

            var wanted = s.Icons.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in view.Items())
            {
                if (!wanted.TryGetValue(it.Id, out var w)) continue;
                seen.Add(it.Id);
                if (fv.GetItemPosition(it.Pidl, out var cur) == 0 && Math.Abs(cur.X - w.X) <= 2 && Math.Abs(cur.Y - w.Y) <= 2) { r.AlreadyInPlace++; continue; }
                int hr = fv.SelectAndPositionItems(1, new[] { it.Pidl }, new[] { new Native.POINT { X = w.X, Y = w.Y } }, SVSI_POSITIONITEM);
                if (hr == 0) r.Moved++; else r.Failed++;
            }
            r.Missing = wanted.Keys.Count(k => !seen.Contains(k));

            if (flags) fv2.SetCurrentFolderFlags(ArrangeMask, s.FolderFlags & ArrangeMask);
            if (fv2 != null && s.Sort.Count > 0) r.ViewRestored |= WriteSort(fv2, s.Sort);
            return r;
        }

        static bool WriteSort(IFolderView2 fv2, List<string> sort)
        {
            try
            {
                var cols = sort.Select(x => x.Split('|')).Where(p => p.Length == 3)
                               .Select(p => new SORTCOLUMN { fmtid = new Guid(p[0]), pid = uint.Parse(p[1]), direction = int.Parse(p[2]) }).ToArray();
                if (cols.Length == 0 || fv2.GetSortColumnCount(out int n) != 0) return false;
                var cur = new SORTCOLUMN[Math.Max(1, n)];
                if (n > 0 && fv2.GetSortColumns(cur, n) == 0 && cur.Take(n).Select(c => $"{c.fmtid:D}|{c.pid}|{c.direction}").SequenceEqual(sort)) return false;
                return fv2.SetSortColumns(cols, cols.Length) == 0;
            }
            catch (Exception ex) { Log.Error("orden", ex); return false; }
        }

        // ---------- utilidades ----------

        static void Check(int hr, string what)
        {
            if (hr != 0) throw new COMException("No se pudo leer el escritorio de Windows. (" + what + ")", hr);
        }

        /// <summary>Las llamadas al Shell van en un hilo STA propio con tiempo límite: si Explorer no responde, WMac no se bloquea.</summary>
        static T RunSta<T>(Func<T> work, int timeoutMs)
        {
            T result = default(T);
            Exception error = null;
            var t = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true, Name = "WMac.DesktopLayout" };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            if (!t.Join(timeoutMs)) throw new TimeoutException("Windows no respondió a tiempo.");
            if (error != null) throw error;
            return result;
        }

        const uint SVSI_POSITIONITEM = 0x80;
        const uint SVGIO_ALLVIEW = 0x2;

        /// <summary>Una vista de iconos del Shell (IFolderView) y su carpeta.</summary>
        sealed class ShellView : IDisposable
        {
            public IFolderView FolderView;
            public IFolderView2 FolderView2;
            IShellFolder _folder;
            readonly List<object> _com = new List<object>();

            /// <summary>
            /// La vista del escritorio de Explorer, como lo documenta Microsoft (IShellWindows::FindWindowSW con SWC_DESKTOP).
            /// Se invoca POR NOMBRE a través de IDispatch: así es imposible llamar por error a otro método de Explorer.
            /// </summary>
            public static ShellView OpenDesktop()
            {
                object shellWindows = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ShellWindows, true));
                try
                {
                    // pvarLoc/pvarLocRoot son VARIANT*: se prueban por valor y, si no, por referencia (ambas formas son válidas en IDispatch).
                    object disp = null;
                    Exception last = null;
                    foreach (bool locByRef in new[] { false, true })
                    {
                        try
                        {
                            var args = new object[] { 0 /* CSIDL_DESKTOP */, null, 8 /* SWC_DESKTOP */, 0, 1 /* SWFO_NEEDDISPATCH */ };
                            var byRef = new ParameterModifier(5);
                            byRef[0] = byRef[1] = locByRef;
                            byRef[3] = true;   // [out] phwnd
                            disp = shellWindows.GetType().InvokeMember("FindWindowSW", BindingFlags.InvokeMethod, null, shellWindows, args,
                                                                       new[] { byRef }, null, null);
                            if (disp != null) break;
                        }
                        catch (TargetInvocationException ex) { last = ex.InnerException ?? ex; }
                        catch (COMException ex) { last = ex; }
                    }
                    if (disp == null) throw new InvalidOperationException("No se encontró el escritorio de Windows." + (last != null ? " (" + last.Message + ")" : ""));
                    try { return FromSite(disp); }
                    finally { Marshal.ReleaseComObject(disp); }
                }
                finally { Marshal.ReleaseComObject(shellWindows); }
            }

            /// <summary>Vista activa del navegador del Shell que expone <paramref name="site"/> (no se libera: es del llamador).</summary>
            public static ShellView FromSite(object site)
            {
                var v = new ShellView();
                try
                {
                    var iid = IID_IShellBrowser;
                    var sid = SID_STopLevelBrowser;
                    var sp = (IComServiceProvider)site;
                    if (sp.QueryService(ref sid, ref iid, out IntPtr pBrowser) != 0 || pBrowser == IntPtr.Zero)
                    {
                        sid = IID_IShellBrowser;   // SID_SShellBrowser
                        Check(sp.QueryService(ref sid, ref iid, out pBrowser), "QueryService");
                    }
                    var browser = (IShellBrowser)Marshal.GetObjectForIUnknown(pBrowser);
                    Marshal.Release(pBrowser);
                    v._com.Add(browser);
                    Check(browser.QueryActiveShellView(out IntPtr pView), "QueryActiveShellView");
                    object view = Marshal.GetObjectForIUnknown(pView);
                    Marshal.Release(pView);
                    v._com.Add(view);
                    v.FolderView = (IFolderView)view;
                    v.FolderView2 = view as IFolderView2;
                    var iidFolder = IID_IShellFolder;
                    Check(v.FolderView.GetFolder(ref iidFolder, out IntPtr pFolder), "GetFolder");
                    v._folder = (IShellFolder)Marshal.GetObjectForIUnknown(pFolder);
                    Marshal.Release(pFolder);
                    v._com.Add(v._folder);
                    return v;
                }
                catch { v.Dispose(); throw; }
            }

            public sealed class Item { public IntPtr Pidl; public string Id, Name; }

            /// <summary>Recorre los iconos. Cada PIDL se libera al avanzar; no conservarlos fuera del bucle.</summary>
            public IEnumerable<Item> Items()
            {
                Check(FolderView.ItemCount(SVGIO_ALLVIEW, out int count), "ItemCount");
                for (int i = 0; i < count; i++)
                {
                    if (FolderView.Item(i, out IntPtr pidl) != 0 || pidl == IntPtr.Zero) continue;
                    try
                    {
                        string id = Name(pidl, 0x8000 /* SHGDN_FORPARSING */);
                        if (string.IsNullOrEmpty(id)) continue;
                        yield return new Item { Pidl = pidl, Id = id, Name = Name(pidl, 0 /* SHGDN_NORMAL */) ?? id };
                    }
                    finally { Native.CoTaskMemFree(pidl); }
                }
            }

            string Name(IntPtr pidl, uint flags)
            {
                IntPtr strret = Marshal.AllocCoTaskMem(1024);
                try
                {
                    if (_folder.GetDisplayNameOf(pidl, flags, strret) != 0) return null;
                    var sb = new StringBuilder(1024);
                    return StrRetToBufW(strret, pidl, sb, sb.Capacity) == 0 ? sb.ToString() : null;
                }
                finally { Marshal.FreeCoTaskMem(strret); }
            }

            public void Dispose()
            {
                for (int i = _com.Count - 1; i >= 0; i--)
                    try { if (_com[i] != null && Marshal.IsComObject(_com[i])) Marshal.ReleaseComObject(_com[i]); } catch { }
                _com.Clear();
            }
        }

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        static extern int StrRetToBufW(IntPtr pstr, IntPtr pidl, StringBuilder buf, int cch);

        static readonly Guid CLSID_ShellWindows = new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
        static readonly Guid SID_STopLevelBrowser = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837");
        static readonly Guid IID_IShellBrowser = new Guid("000214E2-0000-0000-C000-000000000046");
        static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");

        // Interfaces del Shell. Los huecos (_VtblGap) son métodos que WMac nunca llama.

        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IComServiceProvider
        {
            [PreserveSig] int QueryService([In] ref Guid guidService, [In] ref Guid riid, out IntPtr ppvObject);
        }

        [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellBrowser
        {
            void _VtblGap1_12();
            [PreserveSig] int QueryActiveShellView(out IntPtr ppshv);
        }

        [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFolderView
        {
            [PreserveSig] int GetCurrentViewMode(out uint mode);
            void _VtblGap1_1();   // SetCurrentViewMode
            [PreserveSig] int GetFolder([In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int Item(int index, out IntPtr ppidl);
            [PreserveSig] int ItemCount(uint flags, out int count);
            void _VtblGap2_3();   // Items, GetSelectionMarkedItem, GetFocusedItem
            [PreserveSig] int GetItemPosition(IntPtr pidl, out Native.POINT pt);
            [PreserveSig] int GetSpacing(ref Native.POINT pt);
            void _VtblGap3_1();   // GetDefaultSpacing
            [PreserveSig] int GetAutoArrange();
            void _VtblGap4_1();   // SelectItem
            [PreserveSig] int SelectAndPositionItems(uint cidl, [In, MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                                                     [In, MarshalAs(UnmanagedType.LPArray)] Native.POINT[] apt, uint flags);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct SORTCOLUMN { public Guid fmtid; public uint pid; public int direction; }

        [ComImport, Guid("1af3a467-214f-4298-908e-06b03e0b39f9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IFolderView2
        {
            void _VtblGap1_21();  // IFolderView (14) + SetGroupBy … SetText (7)
            [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
            [PreserveSig] int GetCurrentFolderFlags(out uint flags);
            [PreserveSig] int GetSortColumnCount(out int count);
            [PreserveSig] int SetSortColumns([In, MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] cols, int count);
            [PreserveSig] int GetSortColumns([Out, MarshalAs(UnmanagedType.LPArray)] SORTCOLUMN[] cols, int count);
            void _VtblGap2_6();   // GetItem … InvokeVerbOnSelection
            [PreserveSig] int SetViewModeAndIconSize(uint mode, int size);
            [PreserveSig] int GetViewModeAndIconSize(out uint mode, out int size);
        }
    }

    // ===================================================================== datos del respaldo

    [DataContract]
    public sealed class IconPos
    {
        [DataMember(Order = 1)] public string Id;
        [DataMember(Order = 2)] public string Name;
        [DataMember(Order = 3)] public int X;
        [DataMember(Order = 4)] public int Y;
    }

    [DataContract]
    public sealed class FileEntry
    {
        [DataMember(Order = 1)] public string Path;
        [DataMember(Order = 2)] public bool Dir;
        [DataMember(Order = 3)] public long Size;
        [DataMember(Order = 4)] public long Modified;
    }

    [DataContract]
    public sealed class LayoutSnapshot
    {
        [DataMember(Order = 1)] public int Version = 1;
        [DataMember(Order = 2)] public string Taken;
        [DataMember(Order = 3)] public string Reason;
        [DataMember(Order = 4)] public string Screen;
        [DataMember(Order = 5)] public int ViewMode;
        [DataMember(Order = 6)] public int IconSize;
        [DataMember(Order = 7)] public uint FolderFlags;
        [DataMember(Order = 15)] public bool FlagsKnown;
        [DataMember(Order = 8)] public bool AutoArrange;
        [DataMember(Order = 9)] public int SpacingX;
        [DataMember(Order = 10)] public int SpacingY;
        [DataMember(Order = 11)] public List<string> Sort = new List<string>();
        [DataMember(Order = 12)] public List<IconPos> Icons = new List<IconPos>();
        [DataMember(Order = 13)] public List<FileEntry> Files = new List<FileEntry>();
        /// <summary>HKCU\Software\Microsoft\Windows\Shell\Bags\1\Desktop: nombre → "tipo:valor" (ver <see cref="RegValue"/>).</summary>
        [DataMember(Order = 14)] public Dictionary<string, string> Registry = new Dictionary<string, string>();

        public string FilePath;

        /// <summary>Fecha del respaldo para mostrarla («19 de octubre, 16:23»).</summary>
        public string When => DateTime.TryParseExact(Taken, "yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture,
                                                     System.Globalization.DateTimeStyles.None, out var t)
            ? t.ToString("d 'de' MMMM, HH:mm", new System.Globalization.CultureInfo("es-ES"))
            : Taken;

        [OnDeserializing] void Init(StreamingContext c) { Sort = new List<string>(); Icons = new List<IconPos>(); Files = new List<FileEntry>(); Registry = new Dictionary<string, string>(); }
        [OnDeserialized]
        void Fix(StreamingContext c)
        {
            Sort = Sort ?? new List<string>();
            Icons = Icons ?? new List<IconPos>();
            Files = Files ?? new List<FileEntry>();
            Registry = Registry ?? new Dictionary<string, string>();
        }
    }

    internal sealed class LayoutDiff
    {
        public readonly List<string> Moved = new List<string>(), Missing = new List<string>(), Added = new List<string>();
        public readonly List<string> FilesMissing = new List<string>(), FilesAdded = new List<string>(), FilesChanged = new List<string>();
        public bool ViewChanged, ScreenChanged;

        /// <summary>Los iconos siguen donde estaban (los archivos editados o añadidos no cuentan como cambio de distribución).</summary>
        public bool LayoutIntact => Moved.Count == 0 && Missing.Count == 0 && !ViewChanged;

        public string Describe()
        {
            var sb = new StringBuilder();
            if (LayoutIntact) sb.AppendLine("Cada icono está en su sitio.");
            else
            {
                if (Moved.Count > 0) sb.AppendLine($"• Cambiaron de posición: {List(Moved)}");
                if (Missing.Count > 0) sb.AppendLine($"• Ya no están: {List(Missing)}");
                if (ViewChanged) sb.AppendLine("• Cambió la vista (tamaño, orden o alineación de los iconos).");
            }
            if (Added.Count > 0 && !LayoutIntact) sb.AppendLine($"• Nuevos: {List(Added)}");
            if (ScreenChanged && !LayoutIntact) sb.Append("\nLa resolución o la escala de la pantalla cambió desde el respaldo.");
            return sb.ToString().Trim();
        }

        static string List(List<string> items) => string.Join(", ", items.Take(6)) + (items.Count > 6 ? $" y {items.Count - 6} más" : "");
    }

    internal sealed class RestoreResult
    {
        public int Moved, AlreadyInPlace, Missing, Failed;
        public bool ViewRestored;
        public string Error;

        public string Describe() =>
            (Moved > 0 ? $"Tus iconos volvieron a su sitio ({Moved} recolocados)." : "Tus iconos ya estaban en su sitio.") +
            (Missing > 0 ? $"\n\n{Missing} ya no existen en el Escritorio. Si los borraste, búscalos en la Papelera de reciclaje." : "") +
            (Failed > 0 ? $"\n\n{Failed} no se pudieron mover." : "");
    }

    /// <summary>Codifica valores del registro para el respaldo y los escribe en formato .reg (importable sin WMac).</summary>
    internal static class RegValue
    {
        public static string Encode(RegistryValueKind kind, object v)
        {
            switch (kind)
            {
                case RegistryValueKind.DWord: return "dword:" + unchecked((uint)(int)v);
                case RegistryValueKind.QWord: return "qword:" + unchecked((ulong)(long)v);
                case RegistryValueKind.String: return "sz:" + Convert.ToBase64String(Encoding.Unicode.GetBytes((string)v));
                case RegistryValueKind.ExpandString: return "expand:" + Convert.ToBase64String(Encoding.Unicode.GetBytes((string)v));
                case RegistryValueKind.MultiString: return "multi:" + Convert.ToBase64String(Encoding.Unicode.GetBytes(string.Join("\0", (string[])v) + "\0\0"));
                default: return "bin:" + Convert.ToBase64String(v as byte[] ?? new byte[0]);
            }
        }

        /// <summary>Mismo formato que «reg export» (Windows Registry Editor Version 5.00).</summary>
        public static string ToRegFile(string hkcuSubKey, Dictionary<string, string> values)
        {
            var sb = new StringBuilder();
            sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");
            sb.Append("[HKEY_CURRENT_USER\\").Append(hkcuSubKey).Append("]\r\n");
            foreach (var kv in values.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                string name = kv.Key.Length == 0 ? "@" : "\"" + Escape(kv.Key) + "\"";
                int colon = kv.Value.IndexOf(':');
                string type = kv.Value.Substring(0, colon), data = kv.Value.Substring(colon + 1);
                string line;
                switch (type)
                {
                    case "dword": line = "dword:" + uint.Parse(data).ToString("x8"); break;
                    case "qword": line = "hex(b):" + Hex(BitConverter.GetBytes(ulong.Parse(data))); break;
                    case "sz": line = "\"" + Escape(Encoding.Unicode.GetString(Convert.FromBase64String(data))) + "\""; break;
                    case "expand": line = "hex(2):" + Hex(Terminated(Convert.FromBase64String(data))); break;
                    case "multi": line = "hex(7):" + Hex(Convert.FromBase64String(data)); break;
                    default: line = "hex:" + Hex(Convert.FromBase64String(data)); break;
                }
                sb.Append(name).Append('=').Append(line).Append("\r\n");
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        static byte[] Terminated(byte[] utf16) => utf16.Concat(new byte[] { 0, 0 }).ToArray();
        static string Hex(byte[] b) => string.Join(",", b.Select(x => x.ToString("x2")));
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
