using System;
using System.Windows.Interop;

namespace WMac
{
    /// <summary>Ventana oculta de nivel superior que recibe difusiones del sistema (fondo, pantallas, reinicio de Explorer…).</summary>
    internal sealed class BroadcastWindow : IDisposable
    {
        readonly HwndSource _source;
        readonly Action<int, IntPtr, IntPtr> _handler;
        public readonly uint TaskbarCreated;

        public BroadcastWindow(Action<int, IntPtr, IntPtr> handler)
        {
            _handler = handler;
            TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
            var p = new HwndSourceParameters("WMacBroadcast")
            {
                WindowStyle = Native.WS_POPUP,
                ExtendedWindowStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE,
                PositionX = -32000,
                PositionY = -32000,
                Width = 1,
                Height = 1
            };
            _source = new HwndSource(p);
            _source.AddHook(Hook);
        }

        IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_SETTINGCHANGE || msg == Native.WM_DISPLAYCHANGE || msg == Native.WM_TIMECHANGE ||
                msg == Native.WM_POWERBROADCAST || msg == Native.WM_ENDSESSION || (uint)msg == TaskbarCreated)
            {
                try { _handler(msg, wParam, lParam); }
                catch (Exception ex) { Log.Error("broadcast", ex); }
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try { _source.RemoveHook(Hook); _source.Dispose(); } catch { }
        }
    }
}
