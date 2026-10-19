using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Windows;

namespace WMac
{
    /// <summary>
    /// Acciones que el usuario pide explícitamente (abrir, propiedades, copiar, eliminar a la papelera…).
    /// Todas pasan por las API del Shell, con sus diálogos y su "deshacer" habituales.
    /// </summary>
    internal static class ShellOps
    {
        public static readonly string[] ElevatableExt = { ".exe", ".bat", ".cmd", ".msi", ".lnk" };

        public static void Open(DesktopItem it, string verb = null)
        {
            if (it == null) return;
            string file = it.IsSpecial ? "shell:" + it.Path : it.Path;
            string dir = it.IsSpecial ? null : Path.GetDirectoryName(it.Path);
            Execute(file, verb, null, dir);
        }

        public static void Execute(string file, string verb = null, string args = null, string dir = null)
        {
            Native.AllowSetForegroundWindow(Native.ASFW_ANY);
            StaWorker.RunDetached("WMac.Open", () =>
            {
                var sei = new Native.SHELLEXECUTEINFO
                {
                    cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO)),
                    fMask = Native.SEE_MASK_NOASYNC | Native.SEE_MASK_UNICODE,
                    lpVerb = verb,
                    lpFile = file,
                    lpParameters = args,
                    lpDirectory = dir,
                    nShow = Native.SW_SHOWNORMAL
                };
                if (!Native.ShellExecuteEx(ref sei))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 1223) Log.Write($"ShellExecuteEx '{file}' error {err}");
                }
            });
        }

        public static void Properties(DesktopItem it)
        {
            string path = it.Path;
            StaWorker.RunDetached("WMac.Properties", () =>
            {
                if (Native.SHParseDisplayName(path, IntPtr.Zero, out IntPtr pidl, 0, out _) != 0 || pidl == IntPtr.Zero) return;
                try
                {
                    var sei = new Native.SHELLEXECUTEINFO
                    {
                        cbSize = Marshal.SizeOf(typeof(Native.SHELLEXECUTEINFO)),
                        fMask = Native.SEE_MASK_INVOKEIDLIST | Native.SEE_MASK_NOASYNC | Native.SEE_MASK_UNICODE,
                        lpVerb = "properties",
                        lpIDList = pidl,
                        nShow = Native.SW_SHOWNORMAL
                    };
                    Native.ShellExecuteEx(ref sei);
                }
                finally { Native.ILFree(pidl); }
            });
        }

        public static void ShowInExplorer(DesktopItem it)
        {
            if (it.IsSpecial) { Open(it); return; }
            string target = it.Path;
            if (it.Extension == ".lnk")
            {
                string t = ReadShortcutTarget(it.Path);
                if (!string.IsNullOrEmpty(t) && (File.Exists(t) || Directory.Exists(t))) target = t;
            }
            Execute("explorer.exe", null, $"/select,\"{target}\"");
        }

        public static void OpenFolder(string dir) => Execute(dir);

        public static void OpenUri(string uri) => Execute(uri);

        public static void Recycle(IList<string> paths)
        {
            if (paths.Count == 0) return;
            FileOp(Native.FO_DELETE, paths, null, (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING));
        }

        /// <summary>Solo se opera sobre carpetas absolutas que existen (nunca rutas relativas al directorio de trabajo).</summary>
        public static bool IsUsableDir(string dir) =>
            !string.IsNullOrWhiteSpace(dir) && Path.IsPathRooted(dir) && Directory.Exists(dir);

        public static void Transfer(IList<string> paths, string destDir, bool move)
        {
            if (paths.Count == 0 || !IsUsableDir(destDir)) return;
            FileOp(move ? Native.FO_MOVE : Native.FO_COPY, paths, destDir,
                   (ushort)(Native.FOF_ALLOWUNDO | (move ? 0 : Native.FOF_RENAMEONCOLLISION)));
        }

        public static void Rename(string path, string newFullPath)
        {
            if (!Path.IsPathRooted(path) || !Path.IsPathRooted(newFullPath)) return;
            FileOp(Native.FO_RENAME, new[] { path }, newFullPath, Native.FOF_ALLOWUNDO);
        }

        static void FileOp(uint func, IList<string> from, string to, ushort flags)
        {
            var list = from.Where(Path.IsPathRooted).ToArray();
            if (list.Length == 0) return;
            StaWorker.RunDetached("WMac.FileOp", () =>
            {
                var op = new Native.SHFILEOPSTRUCT
                {
                    hwnd = IntPtr.Zero,
                    wFunc = func,
                    pFrom = string.Join("\0", list) + "\0",
                    pTo = to == null ? null : to + "\0",
                    fFlags = flags
                };
                int r = Native.SHFileOperation(ref op);
                if (r != 0 && !op.fAnyOperationsAborted) Log.Write($"SHFileOperation {func} -> 0x{r:X}");
            }, mustFinish: true);
        }

        public static string CreateNewFolder(string dir)
        {
            if (!IsUsableDir(dir)) throw new DirectoryNotFoundException("No se encontró la carpeta del Escritorio.");
            const string baseName = "Nueva carpeta";
            string path = Path.Combine(dir, baseName);
            int n = 2;
            while (Directory.Exists(path) || File.Exists(path)) path = Path.Combine(dir, $"{baseName} ({n++})");
            Directory.CreateDirectory(path);
            return path;
        }

        public static void NewShortcutWizard(string dir)
        {
            if (IsUsableDir(dir)) Execute("rundll32.exe", null, "appwiz.cpl,NewLinkHere " + dir.TrimEnd('\\') + "\\");
        }

        public static void EmptyRecycleBin() =>
            StaWorker.RunDetached("WMac.EmptyBin", () => Native.SHEmptyRecycleBin(IntPtr.Zero, null, 0), mustFinish: true);

        // ---------- portapapeles ----------

        public static void CopyToClipboard(IList<string> paths, bool cut)
        {
            if (paths.Count == 0) return;
            try
            {
                var data = new DataObject();
                var sc = new StringCollection();
                sc.AddRange(paths.ToArray());
                data.SetFileDropList(sc);
                data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)));
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception ex) { Log.Error("clipboard copy", ex); }
        }

        public static void CopyPathsAsText(IList<string> paths)
        {
            try { Clipboard.SetText(string.Join("\r\n", paths.Select(p => "\"" + p + "\""))); }
            catch (Exception ex) { Log.Error("clipboard text", ex); }
        }

        public static bool ClipboardHasFiles()
        {
            try { return Clipboard.ContainsFileDropList(); } catch { return false; }
        }

        public static bool TryGetClipboardFiles(out string[] files, out bool move)
        {
            files = null; move = false;
            try
            {
                if (!Clipboard.ContainsFileDropList()) return false;
                files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                if (Clipboard.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Read(b, 0, 4);
                    int effect = BitConverter.ToInt32(b, 0);
                    move = (effect & 2) != 0 && (effect & 1) == 0;
                }
                return files.Length > 0;
            }
            catch (Exception ex) { Log.Error("clipboard read", ex); return false; }
        }

        // ---------- accesos directos (solo lectura) ----------

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass { }

        public static string ReadShortcutTarget(string lnkPath)
        {
            object link = null;
            try
            {
                link = new ShellLinkCoClass();
                ((IPersistFile)link).Load(lnkPath, 0 /* STGM_READ */);
                var sb = new StringBuilder(1024);
                return ((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0) == 0 ? sb.ToString() : null;
            }
            catch { return null; }
            finally { if (link != null) Marshal.ReleaseComObject(link); }
        }
    }
}
