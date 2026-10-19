using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace WMacSetup
{
    /// <summary>
    /// Instalador por usuario: copia WMac.exe a %LOCALAPPDATA%\Programs\WMac, crea accesos en el
    /// menú Inicio y registra la desinstalación en "Aplicaciones instaladas". No toca nada más.
    /// </summary>
    public partial class SetupWindow : Window
    {
        const string Version = "1.2.0";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WMac";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WMac");
        static string ExePath => Path.Combine(InstallDir, "WMac.exe");
        static string MenuDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WMac");

        bool _done, _installing;

        public SetupWindow()
        {
            InitializeComponent();
            SourceInitialized += (s, e) => ApplyBackdrop();
            Closing += (s, e) => { if (_installing) e.Cancel = true; };   // no cerrar a mitad de la copia
            PathText.Text = InstallDir;
            if (File.Exists(ExePath))
            {
                Heading.Text = "Actualizar WMac Desktop";
                InstallButton.Content = "Actualizar";
            }
            // Instalación nueva: «Iniciar con Windows» marcado. Actualización: se respeta lo que el usuario tenía.
            AutostartBox.IsChecked = File.Exists(ExePath) ? AutostartEnabled() : true;
            CancelButton.Click += (s, e) => Close();
            InstallButton.Click += async (s, e) =>
            {
                if (_done) { Close(); return; }
                await Install();
            };
        }

        async Task Install()
        {
            bool autostart = AutostartBox.IsChecked == true, launch = LaunchBox.IsChecked == true;
            InstallButton.IsEnabled = CancelButton.IsEnabled = false;
            Status.Visibility = Visibility.Visible;
            Status.Text = "Instalando…";
            _installing = true;
            try
            {
                await Task.Run(() =>
                {
                    StopRunningInstance();
                    Directory.CreateDirectory(InstallDir);
                    Extract("WMac.exe", ExePath, required: true);
                    Extract("WMac.exe.config", ExePath + ".config", required: false);

                    Directory.CreateDirectory(MenuDir);
                    CreateShortcut(Path.Combine(MenuDir, "WMac.lnk"), ExePath, "", "WMac Desktop: escritorio moderno con widgets");
                    CreateShortcut(Path.Combine(MenuDir, "Restaurar escritorio original.lnk"), ExePath, "--restore",
                                   "Cierra WMac, desactiva su inicio automático y vuelve al escritorio original de Windows");
                    CreateShortcut(Path.Combine(MenuDir, "Desinstalar WMac.lnk"), ExePath, "--uninstall", "Desinstala WMac Desktop");
                    RegisterUninstall();
                    SetAutostart(autostart);
                });

                _installing = false;
                _done = true;
                OptionsPanel.Visibility = Visibility.Collapsed;
                Heading.Text = "WMac está instalado";
                Sub.Text = "Ábrelo desde el menú Inicio. Si algo no se ve bien al activarlo, se revierte solo en 15 segundos." +
                           (autostart ? " Se abrirá con Windows cuando elijas «Mantener»." : "");
                Status.Text = "Para volver a tu escritorio original: menú Inicio › WMac › Restaurar escritorio original.";
                CancelButton.Visibility = Visibility.Collapsed;
                InstallButton.Content = "Cerrar";
                InstallButton.IsEnabled = true;

                if (launch)
                    Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
            }
            catch (Exception ex)
            {
                _installing = false;
                Status.Text = "No se pudo completar la instalación: " + ex.Message;
                InstallButton.IsEnabled = CancelButton.IsEnabled = true;
            }
        }

        static void StopRunningInstance()
        {
            try { using (var ev = EventWaitHandle.OpenExisting(@"Local\WMac.Desktop.Exit")) ev.Set(); } catch { }
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 6000 && Process.GetProcessesByName("WMac").Length > 0) Thread.Sleep(150);
            foreach (var p in Process.GetProcessesByName("WMac"))
            {
                try { p.Kill(); p.WaitForExit(3000); } catch { }
            }
        }

        static void Extract(string resource, string target, bool required)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null)
                {
                    if (required) throw new FileNotFoundException("Falta el paquete " + resource);
                    return;
                }
                string tmp = target + ".new";
                using (var fs = File.Create(tmp)) s.CopyTo(fs);
                for (int i = 0; ; i++)
                {
                    try
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(tmp, target);
                        return;
                    }
                    catch (IOException) when (i < 20) { Thread.Sleep(250); }
                    catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(250); }
                }
            }
        }

        static void RegisterUninstall()
        {
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "WMac Desktop");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "WMac");
                k.SetValue("DisplayIcon", ExePath);
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", $"\"{ExePath}\" --uninstall");
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                long size = 0;
                try { size = new FileInfo(ExePath).Length / 1024 + 1; } catch { }
                k.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
            }
        }

        static bool AutostartEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    return !string.IsNullOrEmpty(k?.GetValue("WMac") as string);
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("WMac", $"\"{ExePath}\" --autostart");
                else if (k.GetValue("WMac") != null) k.DeleteValue("WMac", false);
            }
            using (var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                if (a?.GetValue("WMac") != null) a.DeleteValue("WMac", false);
        }

        // ---------- accesos directos ----------

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int cch, IntPtr findData, uint flags);
            void GetIDList(out IntPtr pidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int cmd);
            void SetShowCmd(int cmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr hwnd, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLink { }

        static void CreateShortcut(string lnk, string target, string args, string description)
        {
            var link = (IShellLinkW)new ShellLink();
            try
            {
                link.SetPath(target);
                link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                link.SetDescription(description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(lnk, true);
            }
            finally { Marshal.ReleaseComObject(link); }
        }

        // ---------- fondo acrílico de Windows 11 ----------

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        void ApplyBackdrop()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                int dark = 1, round = 2, backdrop = 3;
                DwmSetWindowAttribute(hwnd, 20, ref dark, 4);
                DwmSetWindowAttribute(hwnd, 33, ref round, 4);
                if (Environment.OSVersion.Version.Build >= 22621 && DwmSetWindowAttribute(hwnd, 38, ref backdrop, 4) == 0)
                {
                    HwndSource.FromHwnd(hwnd).CompositionTarget.BackgroundColor = Colors.Transparent;
                    Background = Brushes.Transparent;
                }
            }
            catch { }
        }
    }
}
