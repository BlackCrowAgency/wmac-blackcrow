using System;
using Microsoft.Win32;

namespace WMac
{
    /// <summary>Inicio automático por usuario (HKCU\...\Run). Solo se toca el valor propio "WMac".</summary>
    internal static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string ValueName = "WMac";

        public static string ExePath => System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";

        public static bool IsEnabled()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                    if (string.IsNullOrEmpty(k?.GetValue(ValueName) as string)) return false;
                using (var a = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                    if (a?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) return false; // desactivado en el Administrador de tareas
                return true;
            }
            catch { return false; }
        }

        public static void Set(bool enabled)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (enabled) k.SetValue(ValueName, $"\"{ExePath}\" --autostart");
                    else if (k.GetValue(ValueName) != null) k.DeleteValue(ValueName, false);
                }
                using (var a = Registry.CurrentUser.OpenSubKey(ApprovedKey, true))
                    if (a?.GetValue(ValueName) != null) a.DeleteValue(ValueName, false);
            }
            catch (Exception ex) { Log.Error("autostart", ex); }
        }
    }
}
