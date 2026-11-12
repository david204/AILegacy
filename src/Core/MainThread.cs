using System;
using System.Collections.Concurrent;

namespace AILegacy.Core
{
    /// <summary>
    /// Las peticiones HTTP van en hilos de fondo; todo lo que toca el juego
    /// (UI, héroes, relaciones) se encola aquí y se ejecuta en el tick del juego.
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        public static void Post(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        /// <summary>Ejecuta en el hilo principal y espera el resultado (para el editor web, que corre en otro hilo).</summary>
        public static T Invoke<T>(Func<T> f, int timeoutMs = 10000)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            Post(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
            if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException(L.T("El juego no respondió (¿está cargando o minimizado?)"));
            return tcs.Task.Result;
        }

        public static void Pump()
        {
            int budget = 32;
            while (budget-- > 0 && Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Log.Error("Error ejecutando acción en hilo principal", ex); }
            }
        }
    }
}
