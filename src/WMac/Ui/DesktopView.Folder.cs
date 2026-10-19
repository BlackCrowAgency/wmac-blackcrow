using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WMac
{
    /// <summary>Carpeta virtual abierta: panel de cristal que se expande desde su icono.</summary>
    internal partial class DesktopView
    {
        const double FolderSide = 30, FolderTop = 76, FPad = 16;

        readonly Dictionary<string, LauncherTile> _folderTiles = new Dictionary<string, LauncherTile>();
        List<LauncherEntry> _folderDisplay = new List<LauncherEntry>();
        LauncherGroup _openGroup;
        Rect _panelRect;
        int _fCols = 4, _fRows = 1, _fPage, _fPageCount = 1, _fHole;
        double _fPageW;
        bool _closing;

        // interacción dentro de la carpeta
        LauncherTile _fPress, _fDragTile;
        Point _fPressPoint;
        Vector _fGrab;
        bool _folderDragging, _folderOut;
        List<LauncherEntry> _fArranged;

        void InitFolder()
        {
            FolderOverlay.MouseLeftButtonDown += OnFolderDown;
            FolderOverlay.MouseMove += OnFolderMove;
            FolderOverlay.MouseLeftButtonUp += OnFolderUp;
            FolderOverlay.MouseRightButtonUp += OnFolderRightUp;
            FolderOverlay.LostMouseCapture += OnFolderLostCapture;
            FolderOverlay.SizeChanged += (s, e) => { if (_openGroup != null) LayoutFolder(); };
            FolderTitle.LostKeyboardFocus += (s, e) => CommitFolderTitle();
        }

        Rect PlateRect(LauncherTile t, UIElement relativeTo)
        {
            var tm = t.Metrics ?? _tm;
            var p = t.TranslatePoint(new Point((tm.CellW - tm.Icon) / 2, tm.PlateTop), relativeTo);
            return new Rect(p, new Size(tm.Icon, tm.Icon));
        }

        void OpenFolder(LauncherTile from)
        {
            var g = from?.Entry.Group;
            if (g == null || _m == null) return;
            if (_dragging) EndDrag(false);
            ClearSelection();
            _closing = false;
            _openGroup = g;
            _fPage = 0;
            FolderTitle.Text = g.Name;
            BuildFolderTiles();
            FolderOverlay.Visibility = Visibility.Visible;
            FolderOverlay.UpdateLayout();
            LayoutFolder();

            var src = PlateRect(from, FolderOverlay);
            from.Opacity = 0;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var d = TimeSpan.FromMilliseconds(360);
            FolderScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(src.Width / _panelRect.Width, 1, d) { EasingFunction = ease });
            FolderScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(src.Height / _panelRect.Height, 1, d) { EasingFunction = ease });
            FolderShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(src.X - _panelRect.X, 0, d) { EasingFunction = ease });
            FolderShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(src.Y - _panelRect.Y, 0, d) { EasingFunction = ease });
            FolderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 1, TimeSpan.FromMilliseconds(200)));
            FolderDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.34, TimeSpan.FromMilliseconds(260)));
            foreach (var t in _folderTiles.Values) t.FadeIn(120);
            Keyboard.Focus(this);
        }

        void CloseFolder() => CloseFolder(false);

        void CloseFolder(bool immediate)
        {
            if (_openGroup == null || _closing) return;
            if (_folderDragging) EndFolderDrag(false, default(Point));
            _fPress = null;
            if (FolderTitle.IsKeyboardFocusWithin) CommitFolderTitle();
            var g = _openGroup;
            _openGroup = null;
            _closing = true;
            ClearSelection();
            var from = _tiles.TryGetValue(Launcher.GroupPrefix + g.Id, out var ft) && ft.Visibility == Visibility.Visible ? ft : null;

            void Finish()
            {
                _closing = false;
                FolderOverlay.Visibility = Visibility.Collapsed;
                foreach (var t in _folderTiles.Values) { t.Detach(); FolderStrip.Children.Remove(t); }
                _folderTiles.Clear();
                _folderDisplay = new List<LauncherEntry>();
                FolderScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                FolderScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                FolderShift.BeginAnimation(TranslateTransform.XProperty, null);
                FolderShift.BeginAnimation(TranslateTransform.YProperty, null);
                FolderPanel.BeginAnimation(OpacityProperty, null);
                foreach (var t in _tiles.Values) if (t.Opacity < 1 && t.Parent == Strip) t.Opacity = 1;
                if (IsKeyboardFocusWithin || FolderOverlay.IsKeyboardFocusWithin) Keyboard.Focus(this);
            }

            if (immediate || _panelRect.Width <= 0)
            {
                FolderDim.BeginAnimation(OpacityProperty, null);
                FolderDim.Opacity = 0;
                Finish();
                return;
            }

            var dst = from != null ? PlateRect(from, FolderOverlay)
                                   : new Rect(_panelRect.X + _panelRect.Width / 2 - 30, _panelRect.Y + _panelRect.Height / 2 - 30, 60, 60);
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var d = TimeSpan.FromMilliseconds(250);
            var sx = new DoubleAnimation(dst.Width / _panelRect.Width, d) { EasingFunction = ease };
            sx.Completed += (s, e) => Finish();
            FolderScale.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            FolderScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(dst.Height / _panelRect.Height, d) { EasingFunction = ease });
            FolderShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(dst.X - _panelRect.X, d) { EasingFunction = ease });
            FolderShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(dst.Y - _panelRect.Y, d) { EasingFunction = ease });
            FolderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(230)) { BeginTime = TimeSpan.FromMilliseconds(40) });
            FolderDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)));
        }

        void RefreshFolder()
        {
            if (_openGroup == null) return;
            var g = _launcher.GroupById(_openGroup.Id);
            if (g == null || _launcher.ItemsOf(g).Count == 0) { CloseFolder(true); return; }
            _openGroup = g;
            if (!FolderTitle.IsKeyboardFocusWithin) FolderTitle.Text = g.Name;
            BuildFolderTiles();
            LayoutFolder();
        }

        void CommitFolderTitle()
        {
            if (_openGroup == null) return;
            string name = FolderTitle.Text?.Trim();
            if (string.IsNullOrEmpty(name)) { FolderTitle.Text = _openGroup.Name; return; }
            if (name != _openGroup.Name) _launcher.RenameGroup(_openGroup, name);
        }

        void BuildFolderTiles()
        {
            _folderDisplay = _launcher.ItemsOf(_openGroup).Select(i => new LauncherEntry(i)).ToList();
            var needed = new HashSet<string>(_folderDisplay.Select(e => e.Key));
            foreach (var e in _folderDisplay)
            {
                if (_folderTiles.TryGetValue(e.Key, out var t)) { t.Entry = e; t.Refresh(); continue; }
                t = new IconTile(e, _tm, _effects);
                if (_tileCache != null) t.CacheMode = _tileCache;
                _folderTiles[e.Key] = t;
                FolderStrip.Children.Add(t);
            }
            foreach (var kv in _folderTiles.Where(kv => !needed.Contains(kv.Key)).ToList())
            {
                kv.Value.Detach();
                FolderStrip.Children.Remove(kv.Value);
                _selected.Remove(kv.Value);
                _folderTiles.Remove(kv.Key);
            }
        }

        Point FolderSlot(int i)
        {
            int cap = _fCols * _fRows, page = i / cap, idx = i % cap;
            return new Point(page * _fPageW + FPad + (idx % _fCols) * _tm.CellW, FPad + (idx / _fCols) * _tm.CellH);
        }

        void LayoutFolder()
        {
            if (_openGroup == null || _m == null || _tm == null) return;
            int n = _folderDisplay.Count;
            _fCols = Clamp((int)Math.Ceiling(Math.Sqrt(n * 1.8)), 3, Math.Max(3, Math.Min(6, _m.GridCols)));
            _fRows = Clamp((int)Math.Ceiling(n / (double)_fCols), 1, Math.Max(1, Math.Min(3, _m.GridRows)));
            int cap = _fCols * _fRows;
            _fPageCount = Math.Max(1, (int)Math.Ceiling(n / (double)cap));
            _fPage = Clamp(_fPage, 0, _fPageCount - 1);

            double gridW = _fCols * _tm.CellW, gridH = _fRows * _tm.CellH;
            double pw = gridW + 2 * FolderSide, ph = FolderTop + gridH + (_fPageCount > 1 ? 42 : 24);
            double W = FolderOverlay.ActualWidth > 0 ? FolderOverlay.ActualWidth : _m.W;
            double H = FolderOverlay.ActualHeight > 0 ? FolderOverlay.ActualHeight : _m.H;
            double x = Math.Round((W - pw) / 2), y = Math.Round(Math.Max(_m.Margin, (H - ph) / 2 - 8));
            FolderPanel.Width = pw;
            FolderPanel.Height = ph;
            FolderPanel.Margin = new Thickness(x, y, 0, 0);
            _panelRect = new Rect(x, y, pw, ph);

            FolderGrid.Width = gridW + 2 * FPad;
            FolderGrid.Height = gridH + 2 * FPad;
            FolderGrid.Margin = new Thickness(FolderSide - FPad, FolderTop - FPad, 0, 0);
            _fPageW = FolderGrid.Width;

            for (int i = 0; i < _folderDisplay.Count; i++)
                if (_folderTiles.TryGetValue(_folderDisplay[i].Key, out var t) && t.Parent == FolderStrip)
                {
                    if (t.Metrics != _tm) t.ApplyMetrics(_tm);
                    PlaceTile(t, FolderSlot(i), true, _fPageW);
                }
            FolderStripShift.BeginAnimation(TranslateTransform.XProperty, null);
            FolderStripShift.X = -_fPage * _fPageW;

            FolderDots.Children.Clear();
            if (_fPageCount > 1)
                for (int i = 0; i < _fPageCount; i++)
                {
                    int page = i;
                    var b = new Button { Style = (Style)FindResource("PageDot"), Opacity = i == _fPage ? 0.95 : 0.35 };
                    b.Click += (s, e) => FolderGoToPage(page);
                    FolderDots.Children.Add(b);
                }
        }

        void FolderGoToPage(int page)
        {
            page = Clamp(page, 0, _fPageCount - 1);
            if (page == _fPage) return;
            _fPage = page;
            SlideStrip(FolderStripShift, -_fPage * _fPageW);
            for (int i = 0; i < FolderDots.Children.Count; i++) ((Button)FolderDots.Children[i]).Opacity = i == _fPage ? 0.95 : 0.35;
        }

        int MainIndexAt(Point p)
        {
            if (_m == null) return _launcher.Top.Count;
            int col = Clamp((int)Math.Floor((p.X - _m.Area.X - _m.OffX) / _m.CellW), 0, _m.GridCols - 1);
            int row = Clamp((int)Math.Floor((p.Y - _m.Area.Y - _m.OffY) / _m.CellH), 0, _m.GridRows - 1);
            return Math.Min(_page * _m.Capacity + row * _m.GridCols + col, _launcher.Top.Count);
        }

        // ================= ratón dentro de la carpeta =================

        LauncherTile FolderTileFrom(DependencyObject d)
        {
            var t = Find<LauncherTile>(d);
            return t != null && t.Parent == FolderStrip ? t : null;
        }

        void OnFolderDown(object sender, MouseButtonEventArgs e)
        {
            if (_openGroup == null || _closing) { e.Handled = true; return; }
            var src = e.OriginalSource as DependencyObject;
            if (IsWithin(src, FolderTitle)) return;
            if (!IsWithin(src, FolderPanel) && !IsWithin(src, FolderDragLayer)) { CloseFolder(); e.Handled = true; return; }
            Keyboard.Focus(this);
            var tile = FolderTileFrom(src);
            if (tile == null) { ClearSelection(); e.Handled = true; return; }
            if (e.ClickCount >= 2)
            {
                SelectOnly(tile);
                Activate(tile);
                e.Handled = true;
                return;
            }
            var mods = Keyboard.Modifiers;
            if (mods.HasFlag(ModifierKeys.Control)) Toggle(tile);
            else if (mods.HasFlag(ModifierKeys.Shift) && _anchor != null) SelectRange(_anchor, tile, _folderDisplay, _folderTiles);
            else if (!tile.IsSelected) SelectOnly(tile);
            if (!mods.HasFlag(ModifierKeys.Shift)) _anchor = tile;
            _fPress = tile;
            _fPressPoint = e.GetPosition(FolderOverlay);
            tile.Press(true);
            FolderOverlay.CaptureMouse();
            e.Handled = true;
        }

        void OnFolderMove(object sender, MouseEventArgs e)
        {
            if (_oleDragging || e.LeftButton != MouseButtonState.Pressed || _fPress == null) return;
            if (_openGroup == null || _closing || (!_folderDragging && _fPress.Parent != FolderStrip)) { _fPress = null; return; }
            var p = e.GetPosition(FolderOverlay);
            if (!_folderDragging && (p - _fPressPoint).Length > 6) BeginFolderDrag(p);
            if (_folderDragging) FolderDragMove(p);
        }

        void OnFolderUp(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(FolderOverlay);
            if (_folderDragging) EndFolderDrag(true, p);
            else if (_fPress != null)
            {
                _fPress.Press(false);
                if (Keyboard.Modifiers == ModifierKeys.None && _selected.Count > 1 && _fPress.IsSelected) SelectOnly(_fPress);
            }
            _fPress = null;
            if (FolderOverlay.IsMouseCaptured) FolderOverlay.ReleaseMouseCapture();
        }

        void OnFolderLostCapture(object sender, MouseEventArgs e)
        {
            if (_oleDragging) return;
            if (_folderDragging) EndFolderDrag(false, default(Point));
            _fPress?.Press(false);
            _fPress = null;
        }

        void OnFolderRightUp(object sender, MouseButtonEventArgs e)
        {
            var src = e.OriginalSource as DependencyObject;
            e.Handled = true;
            if (IsWithin(src, FolderTitle)) { e.Handled = false; return; }
            if (!IsWithin(src, FolderPanel)) return;
            var tile = FolderTileFrom(src);
            if (tile != null)
            {
                if (!tile.IsSelected) SelectOnly(tile);
                _anchor = tile;
                ShowMenu(BuildItemMenu(true));
            }
            else ShowMenu(BuildFolderMenu(null));
        }

        void BeginFolderDrag(Point p)
        {
            var t = _fPress;
            t.Press(false);
            if (!t.IsSelected) SelectOnly(t);
            _folderDragging = true;
            _folderOut = false;
            _fDragTile = t;
            var origin = t.TranslatePoint(new Point(0, 0), FolderOverlay);
            _fGrab = p - origin;
            t.StopMotion();
            FolderStrip.Children.Remove(t);
            FolderDragLayer.Children.Add(t);
            Canvas.SetLeft(t, origin.X);
            Canvas.SetTop(t, origin.Y);
            t.IsHitTestVisible = false;
            t.HoverEnabled = false;
            t.Lift(true);
            ToolTipService.SetIsEnabled(t, false);
            _fHole = Math.Max(0, _folderDisplay.FindIndex(x => x.Key == t.Key));
            _fArranged = _folderDisplay.Where(x => x.Key != t.Key).ToList();
            _fArranged.Insert(Math.Min(_fHole, _fArranged.Count), null);
        }

        void FolderDragMove(Point p)
        {
            var t = _fDragTile;
            Canvas.SetLeft(t, p.X - _fGrab.X);
            Canvas.SetTop(t, p.Y - _fGrab.Y);
            if (MaybeStartOleDrag()) return;

            // Fuera del panel: al soltar, el icono sale del grupo (solo cambia la organización de WMac).
            bool outside = !_panelRect.Contains(p);
            if (outside != _folderOut)
            {
                _folderOut = outside;
                FolderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(outside ? 0.45 : 1, TimeSpan.FromMilliseconds(160)));
                FolderDim.BeginAnimation(OpacityProperty, new DoubleAnimation(outside ? 0.08 : 0.34, TimeSpan.FromMilliseconds(160)));
            }
            if (outside) return;

            var origin = FolderGrid.TranslatePoint(new Point(0, 0), FolderOverlay);
            int col = (int)Math.Floor((p.X - origin.X - FPad) / _tm.CellW), row = (int)Math.Floor((p.Y - origin.Y - FPad) / _tm.CellH);
            if (col < 0 || row < 0 || col >= _fCols || row >= _fRows) return;
            int slot = _fPage * _fCols * _fRows + row * _fCols + col;
            int to = Math.Min(slot, _fArranged.Count - 1);
            if (to == _fHole) return;
            _fArranged.RemoveAt(_fHole);
            _fArranged.Insert(to, null);
            _fHole = to;
            for (int i = 0; i < _fArranged.Count; i++)
                if (_fArranged[i] != null && _folderTiles.TryGetValue(_fArranged[i].Key, out var other))
                    PlaceTile(other, FolderSlot(i), true, _fPageW);
        }

        void EndFolderDrag(bool commit, Point p)
        {
            if (!_folderDragging) return;
            _folderDragging = false;
            var t = _fDragTile;
            _fDragTile = null;
            var pos = new Point(Canvas.GetLeft(t), Canvas.GetTop(t));
            FolderDragLayer.Children.Remove(t);
            if (!_folderTiles.TryGetValue(t.Key, out var current) || current != t)
            {
                // La carpeta se cerró o cambió durante el arrastre: el icono no se devuelve.
                t.Detach();
                _folderOut = false;
                return;
            }
            FolderStrip.Children.Add(t);
            var origin = FolderGrid.TranslatePoint(new Point(0, 0), FolderOverlay);
            Canvas.SetLeft(t, pos.X - origin.X + _fPage * _fPageW);
            Canvas.SetTop(t, pos.Y - origin.Y);
            t.IsHitTestVisible = true;
            t.HoverEnabled = true;
            t.Lift(false);
            ToolTipService.SetIsEnabled(t, true);
            bool wasOut = _folderOut;
            _folderOut = false;
            FolderPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
            FolderDim.BeginAnimation(OpacityProperty, new DoubleAnimation(0.34, TimeSpan.FromMilliseconds(120)));

            var group = _openGroup;
            if (commit && wasOut && group != null)
            {
                // Con búsqueda o filtro activos la cuadrícula no es el orden real: se coloca junto al grupo.
                int at = _launcher.Top.FindIndex(x => x.IsGroup && x.Group.Id == group.Id);
                int index = CanReorder ? MainIndexAt(p) : (at < 0 ? _launcher.Top.Count : at + 1);
                CloseFolder(true);
                _launcher.RemoveFromGroup(t.Key, index);
                return;
            }
            if (commit && group != null)
            {
                _launcher.MoveInGroup(group, t.Key, _fHole);
                return;
            }
            RefreshFolder();
            SyncTiles();
        }
    }
}
