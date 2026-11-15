using System;
using System.Collections.Concurrent;

namespace AILegacy.Core
{
    /// <summary>
    /// HTTP requests run on background threads. Anything that touches the game (UI, heroes, relations)
    /// gets queued here and runs on the game tick.
    /// </summary>
    internal static class MainThread
    {
        private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

        public static void Post(Action action)
        {
            if (action != null) Queue.Enqueue(action);
        }

        /// <summary>Runs on the main thread and waits for the result. For the web editor, which runs on another thread.</summary>
        public static T Invoke<T>(Func<T> f, int timeoutMs = 10000)
        {
            var tcs = new System.Threading.Tasks.TaskCompletionSource<T>();
            Post(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
            if (!tcs.Task.Wait(timeoutMs)) throw new TimeoutException(L.T("AIL_3e8e1ac7", "El juego no respondió (¿está cargando o minimizado?)"));
            return tcs.Task.Result;
        }

        public static void Pump()
        {
            int budget = 32;
            while (budget-- > 0 && Queue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { Log.Error("Error running action on main thread", ex); }
            }
        }
    }
}
