using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace WMac
{
    internal sealed class MonitorInfo
    {
        public IntPtr Handle;
        public Native.RECT Bounds;   // píxeles físicos, coordenadas de pantalla
        public Native.RECT Work;
        public bool Primary;
        public uint Dpi = 96;
        public string Device;
        public double Scale => Dpi / 96.0;
    }

    internal static class Monitors
    {
        public static List<MonitorInfo> GetAll()
        {
            var list = new List<MonitorInfo>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Native.RECT r, IntPtr d) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (Native.GetMonitorInfo(h, ref mi))
                {
                    uint dpi = 96;
                    try { if (Native.GetDpiForMonitor(h, 0, out uint dx, out _) == 0 && dx > 0) dpi = dx; } catch { }
                    list.Add(new MonitorInfo
                    {
                        Handle = h,
                        Bounds = mi.rcMonitor,
                        Work = mi.rcWork,
                        Primary = (mi.dwFlags & 1) != 0,
                        Dpi = dpi,
                        Device = mi.szDevice
                    });
                }
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(m => m.Primary).ToList();
        }

        public static Native.RECT VirtualBounds(List<MonitorInfo> list)
        {
            if (list.Count == 0) return new Native.RECT(0, 0, 1920, 1080);
            return new Native.RECT(list.Min(m => m.Bounds.Left), list.Min(m => m.Bounds.Top),
                                   list.Max(m => m.Bounds.Right), list.Max(m => m.Bounds.Bottom));
        }

        public static string Signature(List<MonitorInfo> list) =>
            string.Join("|", list.Select(m => $"{m.Device}:{m.Bounds}/{m.Work}@{m.Dpi}{(m.Primary ? "*" : "")}"));
    }
}
