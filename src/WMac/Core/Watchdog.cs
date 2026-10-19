using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Threading;

namespace WMac
{
    /// <summary>
    /// Red de seguridad: si el hilo de la interfaz deja de responder mientras WMac está integrado en el
    /// escritorio, el proceso se termina. Al terminar, Windows destruye nuestras ventanas y el escritorio
    /// original vuelve a verse tal cual, sin pasos adicionales.
    /// </summary>
    internal static class Watchdog
    {
        const int HangSeconds = 25;
        static int _beat;
        public static volatile bool Armed;

        public static void Start(Dispatcher dispatcher)
        {
            var timer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            timer.Tick += (s, e) => Interlocked.Increment(ref _beat);
            timer.Start();

            var t = new Thread(() =>
            {
                int last = Volatile.Read(ref _beat), stale = 0;
                while (true)
                {
                    Thread.Sleep(1000);
                    int now = Volatile.Read(ref _beat);
                    if (now != last || !Armed) { last = now; stale = 0; continue; }
                    if (++stale >= HangSeconds)
                    {
                        Log.Write("Watchdog: la interfaz no responde; se cierra WMac para dejar visible el escritorio original.");
                        try { Process.GetCurrentProcess().Kill(); } catch { Environment.Exit(3); }
                    }
                }
            }) { IsBackground = true, Name = "WMac.Watchdog" };
            t.Start();
        }
    }
}
