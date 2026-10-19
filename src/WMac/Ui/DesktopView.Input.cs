using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Path = System.IO.Path;

namespace WMac
{
    internal partial class DesktopView
    {
        const string InternalFormat = "WMac.InternalDrag";

        LauncherTile _pressTile, _anchor, _dragTile;
        Point _pressPoint, _marqueeStart;
        Vector _dragGrab;
        bool _dragging, _marquee, _oleDragging, _canReorder;
        HashSet<LauncherTile> _marqueeBase = new HashSet<LauncherTile>();
        List<LauncherEntry> _arranged;
        int _holeIndex, _edgeDir, _wheelAcc;
        LauncherTile _mergeCandidate, _mergeTarget;
        DispatcherTimer _mergeTimer, _edgeTimer;
        DateTime _lastWheelFlip = DateTime.MinValue;

        void InitInput()
        {
            Stage.MouseLeftButtonDown += OnLeftDown;
            Stage.MouseMove += OnMouseMove;
            Stage.MouseLeftButtonUp += OnLeftUp;
            Stage.MouseRightButtonUp += OnRightUp;
            Stage.LostMouseCapture += OnLostCapture;

            _mergeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
            _mergeTimer.Tick += (s, e) =>
            {
                _mergeTimer.Stop();
                if (_mergeCandidate == null) return;
                _mergeTarget = _mergeCandidate;
                _mergeTarget.SetMerge(true);
            };
            _edgeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _edgeTimer.Tick += (s, e) =>
            {
                if (!_dragging || _edgeDir == 0) { _edgeTimer.Stop(); return; }
                int next = _page + _edgeDir;
                bool canGrow = _edgeDir > 0 && next == _pageCount && _arranged != null && _arranged.Count > _m.Capacity * _pageCount - 1;
                if (next >= 0 && (next < _pageCount || canGrow))
                {
                    if (next >= _pageCount) { _pageCount = next + 1; UpdateDots(); }
                    GoToPage(next);
                }
            };
        }

        // ================= utilidades =================

        static T Find<T>(DependencyObject d) where T : class
        {
            while (d != null)
            {
                if (d is T t) return t;
                d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        static bool IsWithin(DependencyObject d, DependencyObject ancestor)
        {
            while (d != null)
            {
                if (d == ancestor) return true;
                d = d is Visual || d is Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            }
            return false;
        }

        bool IsChrome(DependencyObject d) =>
            IsWithin(d, Clock) || IsWithin(d, Music) || IsWithin(d, Weather) || IsWithin(d, Calendar) ||
            IsWithin(d, SearchPill) || IsWithin(d, Chips) || IsWithin(d, Dots);

        LauncherTile MainTileFrom(DependencyObject d)
        {
            var t = Find<LauncherTile>(d);
            return t != null && t.Parent == Strip ? t : null;
        }

        int DisplayIndex(LauncherTile t) => _display.FindIndex(e => e.Key == t.Key);

        // ================= selección =================

        void ClearSelection()
        {
            foreach (var t in _selected) t.IsSelected = false;
            _selected.Clear();
        }

        void SelectOnly(LauncherTile t)
        {
            foreach (var x in _selected) if (x != t) x.IsSelected = false;
            _selected.Clear();
            _selected.Add(t);
            t.IsSelected = true;
        }

        void Toggle(LauncherTile t)
        {
            if (_selected.Remove(t)) t.IsSelected = false;
            else { _selected.Add(t); t.IsSelected = true; }
        }

        void SetSelected(LauncherTile t, bool on)
        {
            if (on) { if (_selected.Add(t)) t.IsSelected = true; }
            else if (_selected.Remove(t)) t.IsSelected = false;
        }

        void SelectRange(LauncherTile a, LauncherTile b, List<LauncherEntry> order, Dictionary<string, LauncherTile> tiles)
        {
            int i = order.FindIndex(e => e.Key == a.Key), j = order.FindIndex(e => e.Key == b.Key);
            if (i < 0 || j < 0) { SelectOnly(b); return; }
            if (i > j) { var x = i; i = j; j = x; }
            ClearSelection();
            for (int k = i; k <= j; k++) if (tiles.TryGetValue(order[k].Key, out var t)) SetSelected(t, true);
        }

        List<LauncherTile> SelectionInOrder()
        {
            var order = _openGroup != null ? _folderDisplay : _display;
            return order.Select(e => _openGroup != null ? (_folderTiles.TryGetValue(e.Key, out var f) ? f : null) : (_tiles.TryGetValue(e.Key, out var t) ? t : null))
                        .Where(t => t != null && _selected.Contains(t)).ToList();
        }

        List<DesktopItem> SelectedItems() => SelectionInOrder().Where(t => !t.Entry.IsGroup).Select(t => t.Entry.Item).ToList();
        List<string> SelectedPaths() => SelectedItems().Where(i => i.IsFileSystem).Select(i => i.Path).ToList();

        void Activate(LauncherTile t)
        {
            if (t.Entry.IsGroup) { OpenFolder(t); return; }
            t.Bounce();
            ShellOps.Open(t.Entry.Item);
        }

        // ================= ratón (cuadrícula principal) =================

        void OnLeftDown(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (IsChrome(src)) return;
            Keyboard.Focus(this);
            var tile = MainTileFrom(src);
            var p = e.GetPosition(Stage);
            var mods = Keyboard.Modifiers;

            if (tile != null)
            {
                if (e.ClickCount >= 2)
                {
                    SelectOnly(tile);
                    Activate(tile);
                    e.Handled = true;
                    return;
                }
                if (mods.HasFlag(ModifierKeys.Control)) Toggle(tile);
                else if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null) SelectRange(_anchor, tile, _display, _tiles);
                else if (!tile.IsSelected) SelectOnly(tile);
                if (!mods.HasFlag(ModifierKeys.Shift)) _anchor = tile;
                _pressTile = tile;
                _pressPoint = p;
                tile.Press(true);
            }
            else
            {
                if (!mods.HasFlag(ModifierKeys.Control)) ClearSelection();
                _marquee = true;
                _marqueeStart = p;
                _marqueeBase = new HashSet<LauncherTile>(_selected);
            }
            Stage.CaptureMouse();
            e.Handled = true;
        }

        void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_oleDragging || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(Stage);
            if (_pressTile != null)
            {
                // Si la vista se reconstruyó tras pulsar, el icono pulsado ya no pertenece a la cuadrícula.
                if (!_dragging && _pressTile.Parent != Strip) { _pressTile = null; return; }
                if (!_dragging && (p - _pressPoint).Length > 6) BeginDrag(p);
                if (_dragging) DragMove(p);
            }
            else if (_marquee) UpdateMarquee(p);
        }

        void OnLeftUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging) EndDrag(true);
            else if (_pressTile != null)
            {
                _pressTile.Press(false);
                if (Keyboard.Modifiers == ModifierKeys.None && _selected.Count > 1 && _pressTile.IsSelected) SelectOnly(_pressTile);
            }
            _pressTile = null;
            EndMarquee();
            if (Stage.IsMouseCaptured) Stage.ReleaseMouseCapture();
        }

        void OnLostCapture(object sender, MouseEventArgs e)
        {
            if (_oleDragging) return;
            if (_dragging) EndDrag(false);
            _pressTile?.Press(false);
            _pressTile = null;
            EndMarquee();
        }

        void OnRightUp(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            if (IsWithin(src, SearchPill)) return;   // el cuadro de texto tiene su propio menú
            Keyboard.Focus(this);
            var tile = MainTileFrom(src);
            if (tile != null)
            {
                if (!tile.IsSelected) SelectOnly(tile);
                _anchor = tile;
                ShowMenu(tile.Entry.IsGroup && _selected.Count == 1 ? BuildFolderMenu(tile) : BuildItemMenu(false));
            }
            else
            {
                ClearSelection();
                ShowMenu(BuildDesktopMenu());
            }
            e.Handled = true;
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Handled) return;
            PageByWheel(e.Delta);
            e.Handled = true;
        }

        // ---- selección por rectángulo ----

        void UpdateMarquee(Point p)
        {
            var r = new Rect(_marqueeStart, p);
            if (r.Width < 4 && r.Height < 4) return;
            Marquee.Visibility = Visibility.Visible;
            Canvas.SetLeft(Marquee, r.X);
            Canvas.SetTop(Marquee, r.Y);
            Marquee.Width = r.Width;
            Marquee.Height = r.Height;
            int cap = _m.Capacity;
            for (int i = 0; i < _display.Count; i++)
            {
                if (i / cap != _page || !_tiles.TryGetValue(_display[i].Key, out var t)) continue;
                var o = StripToStage(SlotPoint(i));
                var plate = new Rect(o.X + (_tm.CellW - _tm.Icon) / 2, o.Y + _tm.PlateTop, _tm.Icon, _tm.Icon);
                SetSelected(t, r.IntersectsWith(plate) || _marqueeBase.Contains(t));
            }
        }

        void EndMarquee()
        {
            if (!_marquee) return;
            _marquee = false;
            Marquee.Visibility = Visibility.Collapsed;
        }

        // ---- arrastrar para reorganizar / agrupar ----

        void BeginDrag(Point p)
        {
            var t = _pressTile;
            t.Press(false);
            if (!t.IsSelected) SelectOnly(t);
            _dragging = true;
            _dragTile = t;
            _canReorder = CanReorder;
            var origin = t.TranslatePoint(new Point(0, 0), Stage);
            _dragGrab = p - origin;
            t.StopMotion();
            Strip.Children.Remove(t);
            DragLayer.Children.Add(t);
            Canvas.SetLeft(t, origin.X);
            Canvas.SetTop(t, origin.Y);
            t.IsHitTestVisible = false;
            t.HoverEnabled = false;
            t.Lift(true);
            ToolTipService.SetIsEnabled(t, false);
            _holeIndex = Math.Max(0, DisplayIndex(t));
            _arranged = _display.Where(x => x.Key != t.Key).ToList();
            _arranged.Insert(Math.Min(_holeIndex, _arranged.Count), null);
        }

        void DragMove(Point p)
        {
            var t = _dragTile;
            Canvas.SetLeft(t, p.X - _dragGrab.X);
            Canvas.SetTop(t, p.Y - _dragGrab.Y);
            if (MaybeStartOleDrag()) return;
            if (!_canReorder) return;

            // Cerca de los bordes de la cuadrícula: cambiar de página tras una breve pausa.
            int dir = p.X < _m.Area.X - 4 ? -1 : p.X > _m.Area.Right + 4 ? 1 : 0;
            if (dir != _edgeDir)
            {
                _edgeDir = dir;
                _edgeTimer.Stop();
                if (dir != 0) _edgeTimer.Start();
            }
            if (dir != 0) return;

            double gx = p.X - _m.Area.X - _m.OffX, gy = p.Y - _m.Area.Y - _m.OffY;
            int col = (int)Math.Floor(gx / _m.CellW), row = (int)Math.Floor(gy / _m.CellH);
            if (col < 0 || row < 0 || col >= _m.GridCols || row >= _m.GridRows) return;
            int slot = _page * _m.Capacity + row * _m.GridCols + col;

            var slotStage = StripToStage(SlotPoint(slot));
            var plateCenter = slotStage + (Vector)_tm.PlateCenter;
            var occupant = slot < _arranged.Count ? _arranged[slot] : null;
            if ((p - plateCenter).Length < _tm.Icon * 0.42 && occupant != null && !t.Entry.IsGroup)
            {
                if (_mergeCandidate == null || _mergeCandidate.Key != occupant.Key)
                {
                    ClearMerge();
                    if (_tiles.TryGetValue(occupant.Key, out var target))
                    {
                        _mergeCandidate = target;
                        _mergeTimer.Start();
                    }
                }
                return;
            }
            ClearMerge();

            int to = Math.Min(slot, _arranged.Count - 1);
            if (to == _holeIndex) return;
            _arranged.RemoveAt(_holeIndex);
            _arranged.Insert(to, null);
            _holeIndex = to;
            for (int i = 0; i < _arranged.Count; i++)
                if (_arranged[i] != null && _tiles.TryGetValue(_arranged[i].Key, out var other))
                    PlaceTile(other, SlotPoint(i), true, _m.PageW);
        }

        void ClearMerge()
        {
            _mergeTimer.Stop();
            _mergeTarget?.SetMerge(false);
            _mergeTarget = null;
            _mergeCandidate = null;
        }

        void EndDrag(bool commit)
        {
            if (!_dragging) return;
            _dragging = false;
            _edgeDir = 0;
            _edgeTimer.Stop();
            var t = _dragTile;
            _dragTile = null;
            var stagePos = new Point(Canvas.GetLeft(t), Canvas.GetTop(t));
            DragLayer.Children.Remove(t);
            if (!_tiles.TryGetValue(t.Key, out var current) || current != t)
            {
                // El icono dejó de mostrarse mientras se arrastraba: no se devuelve a la cuadrícula.
                ClearMerge();
                SyncTiles();
                return;
            }
            Strip.Children.Add(t);
            var stripPos = StageToStrip(stagePos);
            Canvas.SetLeft(t, stripPos.X);
            Canvas.SetTop(t, stripPos.Y);
            t.IsHitTestVisible = true;
            t.HoverEnabled = true;
            t.Lift(false);
            ToolTipService.SetIsEnabled(t, true);

            var merge = _mergeTarget;
            ClearMerge();
            if (commit && _canReorder)
            {
                if (merge != null && merge != t)
                {
                    var g = _launcher.Merge(t.Key, merge.Key);
                    if (g != null) return;   // Launcher.Changed refresca la vista
                }
                else
                {
                    _launcher.MoveTop(t.Key, _holeIndex);
                    return;
                }
            }
            SyncTiles();
        }

        bool MaybeStartOleDrag()
        {
            if (!Native.GetCursorPos(out var pt)) return false;
            var hw = Native.WindowFromPoint(pt);
            if (hw == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(hw, out uint pid);
            if (pid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id) return false;

            // El cursor salió hacia otra ventana: arrastrar y soltar estándar de Windows (copiar/mover a otra app).
            var files = SelectedPaths();
            if (_dragging) EndDrag(false);
            if (_folderDragging) EndFolderDrag(false, default(Point));
            _pressTile = null;
            if (Stage.IsMouseCaptured) Stage.ReleaseMouseCapture();
            if (FolderOverlay.IsMouseCaptured) FolderOverlay.ReleaseMouseCapture();
            if (files.Count == 0) return true;

            var data = new DataObject();
            var sc = new StringCollection();
            sc.AddRange(files.ToArray());
            data.SetFileDropList(sc);
            data.SetData(InternalFormat, "1");
            _oleDragging = true;
            // Con la protección activa el destino solo puede copiar (o crear un acceso directo): el original nunca se mueve.
            var allowed = Protected ? DragDropEffects.Copy | DragDropEffects.Link : DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link;
            try { DragDrop.DoDragDrop(this, data, allowed); }
            catch (Exception ex) { Log.Error("drag out", ex); }
            finally { _oleDragging = false; }
            return true;
        }

        // ================= soltar archivos desde otras apps =================

        DragDropEffects ComputeEffect(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(InternalFormat)) return DragDropEffects.None;
            if (Protected) return DragDropEffects.None;   // no se traen archivos al Escritorio
            if (!_model.CanWriteDesktop || !e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;
            var ks = e.KeyStates;
            bool ctrl = ks.HasFlag(DragDropKeyStates.ControlKey), shift = ks.HasFlag(DragDropKeyStates.ShiftKey), alt = ks.HasFlag(DragDropKeyStates.AltKey);
            // Alt o Ctrl+Mayús piden "crear acceso directo": WMac no lo hace, así que no se mueve ni copia nada.
            if (alt || (ctrl && shift)) return DragDropEffects.None;

            DragDropEffects want;
            if (ctrl) want = DragDropEffects.Copy;
            else if (shift) want = DragDropEffects.Move;
            else
            {
                int preferred = PreferredEffect(e.Data);
                if (preferred != 0 && (preferred & 2) == 0)
                    want = (preferred & 1) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
                else
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    string root = Path.GetPathRoot(_model.UserDesktop);
                    bool sameVolume = files != null && files.Length > 0 && files.All(f => string.Equals(Path.GetPathRoot(f), root, StringComparison.OrdinalIgnoreCase));
                    want = sameVolume ? DragDropEffects.Move : DragDropEffects.Copy;
                }
            }
            if (want == DragDropEffects.None) return want;
            if (!e.AllowedEffects.HasFlag(want))
                want = e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
            return want;
        }

        static int PreferredEffect(IDataObject data)
        {
            try
            {
                if (data.GetDataPresent("Preferred DropEffect") && data.GetData("Preferred DropEffect") is MemoryStream ms && ms.Length >= 4)
                {
                    var b = new byte[4];
                    ms.Position = 0;
                    ms.Read(b, 0, 4);
                    return BitConverter.ToInt32(b, 0);
                }
            }
            catch { }
            return 0;
        }

        protected override void OnDragOver(DragEventArgs e)
        {
            e.Effects = ComputeEffect(e);
            e.Handled = true;
        }

        protected override void OnDragEnter(DragEventArgs e) => OnDragOver(e);

        protected override void OnDrop(DragEventArgs e)
        {
            e.Handled = true;
            if (Protected && !e.Data.GetDataPresent(InternalFormat) && e.Data.GetDataPresent(DataFormats.FileDrop)) { Blocked(); e.Effects = DragDropEffects.None; return; }
            var effect = ComputeEffect(e);
            if (effect == DragDropEffects.None || !(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            bool OnDesktop(string f) => string.Equals(Path.GetDirectoryName(f), _model.UserDesktop, StringComparison.OrdinalIgnoreCase);
            var incoming = effect == DragDropEffects.Copy ? files.ToList() : files.Where(f => !OnDesktop(f)).ToList();
            if (incoming.Count > 0) ShellOps.Transfer(incoming, _model.UserDesktop, effect == DragDropEffects.Move);
            // Si movemos nosotros, el origen no debe borrar nada: se le devuelve "ninguno".
            e.Effects = effect == DragDropEffects.Move ? DragDropEffects.None : effect;
        }

        // ================= teclado =================

        protected override void OnPreviewTextInput(TextCompositionEventArgs e)
        {
            base.OnPreviewTextInput(e);
            if (e.Handled || SearchBox.IsKeyboardFocusWithin || FolderTitle.IsKeyboardFocusWithin) return;
            if (_dragging || _folderDragging) { e.Handled = true; return; }
            if (string.IsNullOrEmpty(e.Text) || !char.IsLetterOrDigit(e.Text[0])) return;
            if (_openGroup != null) CloseFolder();
            // Escribir en cualquier parte empieza una búsqueda.
            SearchBox.Focus();
            SearchBox.Text = e.Text;
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control);

            if (e.OriginalSource is TextBox box)
            {
                // Dentro de un cuadro de texto nunca se ejecutan atajos de archivos (p. ej. Supr).
                if (box == SearchBox)
                {
                    if (key == Key.Escape) { ClearSearch(false); e.Handled = true; }
                    else if (key == Key.Enter && _display.Count > 0 && _tiles.TryGetValue(_display[0].Key, out var first)) { Activate(first); e.Handled = true; }
                    else if (key == Key.Down && _display.Count > 0 && _tiles.TryGetValue(_display[0].Key, out var sel)) { Keyboard.Focus(this); SelectOnly(sel); _anchor = sel; e.Handled = true; }
                }
                else if (box == FolderTitle && (key == Key.Enter || key == Key.Escape))
                {
                    if (key == Key.Escape) FolderTitle.Text = _openGroup?.Name ?? "";
                    CommitFolderTitle();
                    Keyboard.Focus(this);
                    e.Handled = true;
                }
                return;
            }

            if (_dragging || _folderDragging) { e.Handled = true; return; }   // nada de atajos a mitad de un arrastre

            bool handled = true;
            switch (key)
            {
                case Key.Escape:
                    if (_openGroup != null) CloseFolder();
                    else if (_query.Length > 0 || _filter != null) ClearSearch(false);
                    else ClearSelection();
                    break;
                case Key.F when ctrl: SearchBox.Focus(); break;
                case Key.Enter when mods.HasFlag(ModifierKeys.Alt):
                    var item = SelectedItems().FirstOrDefault();
                    if (item != null) ShellOps.Properties(item);
                    break;
                case Key.Enter:
                    foreach (var t in SelectionInOrder().Take(16)) Activate(t);
                    break;
                case Key.Delete: DeleteSelected(); break;
                case Key.F2: RenameSelected(); break;
                case Key.F5: Refresh(); break;
                case Key.A when ctrl:
                    foreach (var t in (_openGroup != null ? _folderTiles.Values : _tiles.Values)) if (t.Visibility == Visibility.Visible) SetSelected(t, true);
                    break;
                case Key.C when ctrl: ShellOps.CopyToClipboard(SelectedPaths(), false); break;
                case Key.X when ctrl: if (!Blocked()) ShellOps.CopyToClipboard(SelectedPaths(), true); break;
                case Key.V when ctrl: Paste(); break;
                case Key.N when ctrl && mods.HasFlag(ModifierKeys.Shift): NewFolder(); break;
                case Key.Apps:
                case Key.F10 when mods.HasFlag(ModifierKeys.Shift):
                    ShowMenu(_selected.Count > 0 ? BuildItemMenu(_openGroup != null) : BuildDesktopMenu());
                    break;
                case Key.PageDown: GoToPage(_page + 1); break;
                case Key.PageUp: GoToPage(_page - 1); break;
                case Key.Left when ctrl: GoToPage(_page - 1); break;
                case Key.Right when ctrl: GoToPage(_page + 1); break;
                case Key.Left: MoveSelection(-1); break;
                case Key.Right: MoveSelection(1); break;
                case Key.Up: MoveSelection(-(_openGroup != null ? _fCols : _m?.GridCols ?? 1)); break;
                case Key.Down: MoveSelection(_openGroup != null ? _fCols : _m?.GridCols ?? 1); break;
                default: handled = false; break;
            }
            if (handled) e.Handled = true;
        }

        void MoveSelection(int delta)
        {
            var order = _openGroup != null ? _folderDisplay : _display;
            var tiles = _openGroup != null ? _folderTiles : _tiles;
            if (order == null || order.Count == 0) return;
            var cur = _anchor != null && _selected.Contains(_anchor) ? _anchor : SelectionInOrder().FirstOrDefault();
            int index = cur == null ? 0 : order.FindIndex(e => e.Key == cur.Key) + delta;
            index = Clamp(index, 0, order.Count - 1);
            if (!tiles.TryGetValue(order[index].Key, out var next)) return;
            SelectOnly(next);
            _anchor = next;
            if (_openGroup != null) FolderGoToPage(index / Math.Max(1, _fCols * _fRows));
            else if (_m != null) GoToPage(index / _m.Capacity);
        }
    }
}
