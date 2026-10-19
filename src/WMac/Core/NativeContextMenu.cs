using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace WMac
{
    /// <summary>Muestra el menú contextual clásico del Shell ("Mostrar más opciones").</summary>
    internal static class NativeContextMenu
    {
        const uint CMF_NORMAL = 0x0, CMF_EXTENDEDVERBS = 0x100;
        const uint CMIC_MASK_UNICODE = 0x4000, CMIC_MASK_PTINVOKE = 0x20000000,
                   CMIC_MASK_SHIFT_DOWN = 0x10000000, CMIC_MASK_CONTROL_DOWN = 0x40000000;

        static IContextMenu2 _cm2;
        static IContextMenu3 _cm3;

        public static void Show(IntPtr owner, IList<string> paths)
        {
            if (owner == IntPtr.Zero || paths == null || paths.Count == 0) return;
            Native.GetCursorPos(out var pt);

            // Solo elementos de la misma carpeta pueden compartir un menú.
            string firstDir = paths[0].StartsWith("::") ? null : Path.GetDirectoryName(paths[0]);
            var group = firstDir == null
                ? new List<string> { paths[0] }
                : paths.Where(p => !p.StartsWith("::") && string.Equals(Path.GetDirectoryName(p), firstDir, StringComparison.OrdinalIgnoreCase)).ToList();

            var pidls = new List<IntPtr>();
            IShellFolder folder = null;
            IContextMenu menu = null;
            IntPtr hmenu = IntPtr.Zero;
            HwndSource src = null;
            try
            {
                foreach (var p in group)
                    if (Native.SHParseDisplayName(p, IntPtr.Zero, out IntPtr pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
                        pidls.Add(pidl);
                if (pidls.Count == 0) return;

                var children = new IntPtr[pidls.Count];
                Guid iidFolder = typeof(IShellFolder).GUID;
                for (int i = 0; i < pidls.Count; i++)
                {
                    if (Native.SHBindToParent(pidls[i], ref iidFolder, out IntPtr pf, out IntPtr child) != 0) return;
                    if (i == 0) folder = (IShellFolder)Marshal.GetObjectForIUnknown(pf);
                    Marshal.Release(pf);
                    children[i] = child; // apunta dentro de pidls[i]
                }

                Guid iidMenu = typeof(IContextMenu).GUID;
                if (folder.GetUIObjectOf(owner, (uint)children.Length, children, ref iidMenu, IntPtr.Zero, out IntPtr pcm) != 0 || pcm == IntPtr.Zero) return;
                menu = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
                Marshal.Release(pcm);
                _cm2 = menu as IContextMenu2;
                _cm3 = menu as IContextMenu3;

                hmenu = Native.CreatePopupMenu();
                bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
                menu.QueryContextMenu(hmenu, 0, 1, 0x7FFF, CMF_NORMAL | (shift ? CMF_EXTENDEDVERBS : 0));

                src = HwndSource.FromHwnd(owner);
                src?.AddHook(Hook);
                uint cmd = Native.TrackPopupMenuEx(hmenu, Native.TPM_RETURNCMD | Native.TPM_RIGHTBUTTON, pt.X, pt.Y, owner, IntPtr.Zero);
                src?.RemoveHook(Hook);
                src = null;

                if (cmd > 0)
                {
                    uint mask = CMIC_MASK_UNICODE | CMIC_MASK_PTINVOKE;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) mask |= CMIC_MASK_CONTROL_DOWN;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) mask |= CMIC_MASK_SHIFT_DOWN;
                    var info = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                        fMask = mask,
                        hwnd = owner,
                        lpVerb = (IntPtr)(cmd - 1),
                        lpVerbW = (IntPtr)(cmd - 1),
                        lpDirectoryW = firstDir,
                        nShow = Native.SW_SHOWNORMAL,
                        ptInvoke = pt
                    };
                    menu.InvokeCommand(ref info);
                }
            }
            catch (Exception ex) { Log.Error("native menu", ex); }
            finally
            {
                src?.RemoveHook(Hook);
                _cm2 = null; _cm3 = null;
                if (hmenu != IntPtr.Zero) Native.DestroyMenu(hmenu);
                if (menu != null) Marshal.ReleaseComObject(menu);
                if (folder != null) Marshal.ReleaseComObject(folder);
                foreach (var p in pidls) Native.ILFree(p);
            }
        }

        static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != Native.WM_INITMENUPOPUP && msg != Native.WM_DRAWITEM && msg != Native.WM_MEASUREITEM && msg != Native.WM_MENUCHAR)
                return IntPtr.Zero;
            try
            {
                if (_cm3 != null)
                {
                    if (_cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out IntPtr result) == 0) { handled = true; return result; }
                }
                else if (_cm2 != null && msg != Native.WM_MENUCHAR)
                {
                    if (_cm2.HandleMenuMsg((uint)msg, wParam, lParam) == 0) { handled = true; return IntPtr.Zero; }
                }
            }
            catch { }
            return IntPtr.Zero;
        }
    }
}
