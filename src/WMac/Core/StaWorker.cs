using System;
using System.Collections.Concurrent;
using System.Threading;

namespace WMac
{
    /// <summary>Hilo STA en segundo plano para llamadas al Shell (iconos, escaneo) sin bloquear la interfaz.</summary>
    internal sealed class StaWorker : IDisposable
    {
        readonly BlockingCollection<Action> _queue = new BlockingCollection<Action>();

        public StaWorker(string name)
        {
            var t = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        void Run()
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Log.Error("worker", ex); }
            }
        }

        public void Post(Action action)
        {
            try { if (!_queue.IsAddingCompleted) _queue.Add(action); } catch (InvalidOperationException) { }
        }

        public void Dispose() => _queue.CompleteAdding();

        /// <summary>Ejecuta una acción en un hilo STA nuevo (operaciones que pueden tardar: abrir, copiar, borrar).</summary>
        /// <param name="mustFinish">true para operaciones de archivos: el proceso no termina hasta que acaben.</param>
        public static void RunDetached(string name, Action action, bool mustFinish = false)
        {
            var t = new Thread(() =>
            {
                try { action(); }
                catch (Exception ex) { Log.Error(name, ex); }
            }) { IsBackground = !mustFinish, Name = name };
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }
    }
}
