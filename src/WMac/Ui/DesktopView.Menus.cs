using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace WMac
{
    internal partial class DesktopView
    {
        IntPtr OwnerHwnd => (PresentationSource.FromVisual(this) as HwndSource)?.Handle ?? IntPtr.Zero;

        void ShowMenu(ContextMenu cm)
        {
            cm.PlacementTarget = this;
            cm.Placement = PlacementMode.MousePoint;
            cm.IsOpen = true;
        }

        static MenuItem MI(string header, Action click = null, bool enabled = true, bool isChecked = false, string gesture = null, bool bold = false)
        {
            var mi = new MenuItem { Header = header, IsEnabled = enabled, IsChecked = isChecked, InputGestureText = gesture ?? "" };
            if (bold) mi.FontWeight = FontWeights.SemiBold;
            if (click != null) mi.Click += (s, e) => click();
            return mi;
        }

        // ================= protección de archivos =================

        static bool Protected => SettingsStore.Current.ProtectFiles;

        /// <summary>Con la protección activa, una acción que cambiaría archivos no se hace (y se avisa como mucho una vez cada 30 s).</summary>
        static bool Blocked()
        {
            if (!Protected) return false;
            App.Instance?.NotifyProtected();
            return true;
        }

        // ================= menús =================

        ContextMenu BuildItemMenu(bool inFolder)
        {
            var cm = new ContextMenu();
            var tiles = SelectionInOrder();
            var items = tiles.Where(t => !t.Entry.IsGroup).Select(t => t.Entry.Item).ToList();
            if (tiles.Count == 1 && tiles[0].Entry.IsGroup) return BuildFolderMenu(tiles[0]);
            if (items.Count == 0) return BuildDesktopMenu();
            var first = items[0];
            bool single = items.Count == 1;
            bool allFs = items.All(i => i.IsFileSystem);
            var paths = items.Select(i => i.Path).ToList();

            cm.Items.Add(MI("Abrir", () => { foreach (var t in tiles.Take(16)) Activate(t); }, gesture: "Entrar", bold: true));
            if (single && first.IsFileSystem && ShellOps.ElevatableExt.Contains(first.Extension))
                cm.Items.Add(MI("Ejecutar como administrador", () => ShellOps.Open(first, "runas")));
            if (single && first.IsFileSystem)
                cm.Items.Add(MI(first.Extension == ".lnk" ? "Abrir ubicación del archivo" : "Mostrar en el Explorador", () => ShellOps.ShowInExplorer(first)));
            if (single && first.IsRecycleBin && !Protected)
                cm.Items.Add(MI("Vaciar Papelera de reciclaje", ShellOps.EmptyRecycleBin, enabled: !ShellIcons.IsRecycleBinEmpty()));

            // Organización virtual (no toca los archivos).
            var keys = items.Select(i => i.Key).ToList();
            if (inFolder && _openGroup != null)
            {
                cm.Items.Add(new Separator());
                var group = _openGroup;
                int at = _launcher.Top.FindIndex(e => e.IsGroup && e.Group.Id == group.Id);
                cm.Items.Add(MI("Quitar del grupo", () =>
                {
                    int index = at < 0 ? _launcher.Top.Count : at + 1;
                    foreach (var k in keys) _launcher.RemoveFromGroup(k, index++);
                }));
            }
            else if (CanReorder && items.Count >= 2)
            {
                cm.Items.Add(new Separator());
                cm.Items.Add(MI("Agrupar selección", () =>
                {
                    int index = tiles.Select(DisplayIndex).Where(i => i >= 0).DefaultIfEmpty(0).Min();
                    _launcher.GroupSelection(keys, index);
                }));
            }

            if (allFs)
            {
                cm.Items.Add(new Separator());
                if (!Protected) cm.Items.Add(MI("Cortar", () => ShellOps.CopyToClipboard(paths, true), gesture: "Ctrl+X"));
                cm.Items.Add(MI("Copiar", () => ShellOps.CopyToClipboard(paths, false), gesture: "Ctrl+C"));
                cm.Items.Add(MI("Copiar como ruta de acceso", () => ShellOps.CopyPathsAsText(paths)));
                if (!Protected)
                {
                    cm.Items.Add(new Separator());
                    if (single) cm.Items.Add(MI("Cambiar nombre", RenameSelected, gesture: "F2"));
                    cm.Items.Add(MI("Eliminar", DeleteSelected, gesture: "Supr"));
                }
            }

            cm.Items.Add(new Separator());
            if (single) cm.Items.Add(MI("Propiedades", () => ShellOps.Properties(first), gesture: "Alt+Entrar"));
            // El menú clásico de Windows puede eliminar, mover o renombrar: con la protección activa no se ofrece.
            if (!Protected)
                cm.Items.Add(MI("Mostrar más opciones", () =>
                    Dispatcher.BeginInvoke(DispatcherPriority.Input, (Action)(() => NativeContextMenu.Show(OwnerHwnd, paths)))));
            else cm.Items.Add(MI("Archivos protegidos (solo lectura)…", () => App.Instance?.ShowSettings()));
            return cm;
        }

        /// <summary>Menú de una carpeta virtual (o del fondo de la carpeta abierta).</summary>
        ContextMenu BuildFolderMenu(LauncherTile tile)
        {
            var group = tile?.Entry.Group ?? _openGroup;
            var cm = new ContextMenu();
            if (group == null) return cm;
            if (tile != null) cm.Items.Add(MI("Abrir", () => OpenFolder(tile), bold: true));
            cm.Items.Add(MI("Cambiar nombre del grupo", () => RenameGroupDialog(group)));
            cm.Items.Add(MI("Desagrupar", () => { if (_openGroup != null) CloseFolder(true); _launcher.Ungroup(group); }));
            return cm;
        }

        ContextMenu BuildDesktopMenu()
        {
            var s = SettingsStore.Current;
            var cm = new ContextMenu();

            var view = MI("Ver");
            view.Items.Add(MI("Iconos extra grandes", () => SetIconSize("xlarge"), isChecked: s.IconSize == "xlarge"));
            view.Items.Add(MI("Iconos grandes", () => SetIconSize("large"), isChecked: s.IconSize == "large"));
            view.Items.Add(MI("Iconos medianos", () => SetIconSize("medium"), isChecked: s.IconSize == "medium"));
            view.Items.Add(MI("Iconos pequeños", () => SetIconSize("small"), isChecked: s.IconSize == "small"));
            view.Items.Add(new Separator());
            view.Items.Add(MI("Mostrar iconos", () => ToggleSetting(x => x.ShowIcons = !x.ShowIcons), isChecked: s.ShowIcons));
            cm.Items.Add(view);

            var sort = MI("Ordenar por");
            sort.Items.Add(MI("Categoría", () => _launcher.SortTop("category")));
            sort.Items.Add(MI("Nombre", () => _launcher.SortTop("name")));
            sort.Items.Add(MI("Fecha de modificación", () => _launcher.SortTop("date")));
            cm.Items.Add(sort);
            cm.Items.Add(MI("Organizar automáticamente…", ConfirmOrganize));
            cm.Items.Add(MI("Buscar", () => SearchBox.Focus(), gesture: "Ctrl+F"));
            cm.Items.Add(MI("Actualizar", Refresh, gesture: "F5"));

            if (!Protected)
            {
                cm.Items.Add(new Separator());
                cm.Items.Add(MI("Pegar", Paste, enabled: ShellOps.ClipboardHasFiles() && _model.CanWriteDesktop, gesture: "Ctrl+V"));
                var nw = MI("Nuevo");
                nw.Items.Add(MI("Carpeta", NewFolder, gesture: "Ctrl+Mayús+N"));
                nw.Items.Add(MI("Acceso directo…", () => ShellOps.NewShortcutWizard(_model.UserDesktop)));
                cm.Items.Add(nw);
            }

            cm.Items.Add(new Separator());
            var widgets = MI("Widgets");
            widgets.Items.Add(MI("Reloj", () => ToggleSetting(x => x.ShowClock = !x.ShowClock), isChecked: s.ShowClock));
            widgets.Items.Add(MI("Música", () => ToggleSetting(x => x.ShowMusic = !x.ShowMusic), isChecked: s.ShowMusic));
            widgets.Items.Add(MI("Clima", () => ToggleSetting(x => x.ShowWeather = !x.ShowWeather), isChecked: s.ShowWeather));
            widgets.Items.Add(MI("Calendario", () => ToggleSetting(x => x.ShowCalendar = !x.ShowCalendar), isChecked: s.ShowCalendar));
            cm.Items.Add(widgets);
            cm.Items.Add(MI("Abrir carpeta Escritorio", () => ShellOps.OpenFolder(_model.UserDesktop), enabled: _model.CanWriteDesktop));
            cm.Items.Add(MI("Fondo de pantalla…", () => ShellOps.OpenUri("ms-settings:personalization-background")));
            cm.Items.Add(MI("Configuración de pantalla", () => ShellOps.OpenUri("ms-settings:display")));

            cm.Items.Add(new Separator());
            cm.Items.Add(MI("Ajustes de WMac…", () => App.Instance?.ShowSettings()));
            if (_preview)
            {
                cm.Items.Add(MI("Activar en el escritorio", () => App.Instance?.ActivateDesktop()));
                cm.Items.Add(MI("Cerrar vista previa", () => Window.GetWindow(this)?.Close()));
            }
            else cm.Items.Add(MI("Mostrar escritorio original", () => App.Instance?.DeactivateDesktop()));
            return cm;
        }

        // ================= acciones =================

        void ConfirmOrganize()
        {
            MessageDialog.Open("Organizar automáticamente",
                "Los iconos se ordenarán por categoría y se desharán tus grupos. Tus archivos no se mueven.",
                new[] { "Organizar", "Cancelar" },
                r => { if (r == "Organizar") { if (_openGroup != null) CloseFolder(true); _launcher.BuildDefault(); } });
        }

        void RenameGroupDialog(LauncherGroup g)
        {
            RenameDialog.Ask(g.Name, g.Name.Length, name => _launcher.RenameGroup(g, name));
        }

        void DeleteSelected()
        {
            if (Blocked()) return;
            var paths = SelectedPaths();
            if (paths.Count > 0) ShellOps.Recycle(paths);
        }

        void RenameSelected()
        {
            var tiles = SelectionInOrder();
            if (tiles.Count != 1) return;
            if (tiles[0].Entry.IsGroup) { RenameGroupDialog(tiles[0].Entry.Group); return; }   // los grupos son virtuales
            if (Blocked()) return;
            RenameItem(tiles[0].Entry.Item);
        }

        void RenameItem(DesktopItem it)
        {
            if (it == null || !it.IsFileSystem || Blocked()) return;
            string fileName = Path.GetFileName(it.Path);
            bool extVisible = it.IsFolder || string.Equals(it.Name, fileName, StringComparison.OrdinalIgnoreCase);
            string ext = it.IsFolder ? "" : Path.GetExtension(it.Path);
            string current = extVisible ? fileName : it.Name;
            int selLen = extVisible && !it.IsFolder && ext.Length > 0 ? current.Length - ext.Length : current.Length;

            RenameDialog.Ask(current, selLen, newName =>
            {
                newName = (newName ?? "").Trim().TrimEnd('.');
                if (newName.Length == 0 || newName == current) return;
                if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    MessageDialog.Info("Nombre no válido", "Un nombre de archivo no puede contener los caracteres  \\ / : * ? \" < > |");
                    return;
                }
                ShellOps.Rename(it.Path, Path.Combine(Path.GetDirectoryName(it.Path) ?? "", extVisible ? newName : newName + ext));
            });
        }

        void OnRenameRequested(DesktopItem item)
        {
            if (_myPendingRename == null || !string.Equals(_myPendingRename, item.Path, StringComparison.OrdinalIgnoreCase)) return;
            _myPendingRename = null;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, (Action)(() => RenameItem(item)));
        }

        void NewFolder()
        {
            if (Blocked()) return;
            if (!_model.CanWriteDesktop)
            {
                MessageDialog.Info("No se encontró el Escritorio", "Tu carpeta Escritorio no está disponible ahora mismo.");
                return;
            }
            try
            {
                string path = ShellOps.CreateNewFolder(_model.UserDesktop);
                _myPendingRename = path;
                _model.RequestRenameWhenAppears(path);
                _model.ScheduleRescan();
            }
            catch (Exception ex)
            {
                Log.Error("new folder", ex);
                MessageDialog.Info("No se pudo crear la carpeta", ex.Message);
            }
        }

        void Paste()
        {
            if (Blocked()) return;
            if (!_model.CanWriteDesktop || !ShellOps.TryGetClipboardFiles(out var files, out bool move)) return;
            var list = move
                ? files.Where(f => !string.Equals(Path.GetDirectoryName(f), _model.UserDesktop, StringComparison.OrdinalIgnoreCase)).ToList()
                : files.ToList();
            if (list.Count == 0) return;
            ShellOps.Transfer(list, _model.UserDesktop, move);
            if (move) { try { Clipboard.Clear(); } catch { } }
        }

        void SetIconSize(string size)
        {
            SettingsStore.Current.IconSize = size;
            SettingsStore.NotifyChanged();
        }

        void ToggleSetting(Action<AppSettings> change)
        {
            change(SettingsStore.Current);
            SettingsStore.NotifyChanged();
        }

        void Refresh()
        {
            _model.Rescan();
            _model.ReloadIcons();
        }
    }
}
