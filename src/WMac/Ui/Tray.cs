using System;
using System.Windows;
using WF = System.Windows.Forms;

namespace WMac
{
    /// <summary>Icono en la bandeja: activar/desactivar, ajustes, inicio automático y restauración.</summary>
    internal sealed class Tray : IDisposable
    {
        readonly WF.NotifyIcon _icon;
        readonly WF.ContextMenuStrip _menu;
        readonly WF.ToolStripMenuItem _toggle, _autostart;

        public Tray()
        {
            _menu = new WF.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = new System.Drawing.Font("Segoe UI", 9.5f) };
            var header = new WF.ToolStripMenuItem("WMac Desktop") { Enabled = false, Font = new System.Drawing.Font("Segoe UI", 9.5f, System.Drawing.FontStyle.Bold) };
            _toggle = new WF.ToolStripMenuItem("Activar escritorio WMac", null, (s, e) => App.Instance?.ToggleDesktop());
            var preview = new WF.ToolStripMenuItem("Vista previa en ventana", null, (s, e) => App.Instance?.ShowPreview());
            var settings = new WF.ToolStripMenuItem("Ajustes…", null, (s, e) => App.Instance?.ShowSettings());
            _autostart = new WF.ToolStripMenuItem("Iniciar con Windows", null, (s, e) => App.Instance?.SetAutostart(!Autostart.IsEnabled()));
            var check = new WF.ToolStripMenuItem("Comprobar mi escritorio…", null, (s, e) => App.Instance?.CheckDesktopNow());
            var restore = new WF.ToolStripMenuItem("Restaurar escritorio original", null, (s, e) => App.Instance?.RestoreOriginal());
            var exit = new WF.ToolStripMenuItem("Salir", null, (s, e) => App.Instance?.ExitApp());
            _menu.Items.AddRange(new WF.ToolStripItem[]
            {
                header, new WF.ToolStripSeparator(), _toggle, preview, settings, new WF.ToolStripSeparator(),
                _autostart, new WF.ToolStripSeparator(), check, restore, exit
            });
            _menu.Opening += (s, e) => Update();

            _icon = new WF.NotifyIcon { Icon = LoadIcon(), Text = "WMac Desktop", ContextMenuStrip = _menu, Visible = true };
            _icon.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) App.Instance?.ShowSettings(); };
        }

        static System.Drawing.Icon LoadIcon()
        {
            try
            {
                var sri = Application.GetResourceStream(new Uri("pack://application:,,,/WMac;component/Assets/WMac.ico"));
                using (sri.Stream) return new System.Drawing.Icon(sri.Stream, WF.SystemInformation.SmallIconSize);
            }
            catch { return System.Drawing.SystemIcons.Application; }
        }

        public void Update()
        {
            _toggle.Text = App.Instance?.Host?.IsEnabled == true ? "Mostrar escritorio original" : "Activar escritorio WMac";
            _autostart.Checked = Autostart.IsEnabled();
        }

        public void Notify(string title, string text)
        {
            try { _icon.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.Info); } catch { }
        }

        public void Dispose()
        {
            _icon.Visible = false;
            _icon.Dispose();
            _menu.Dispose();
        }
    }
}
