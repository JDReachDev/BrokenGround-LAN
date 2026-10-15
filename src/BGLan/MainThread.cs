using System;
using System.Collections.Generic;
using System.Threading;

namespace BGLan
{
    /// <summary>Runs work queued from network threads on Unity's main thread (drained in LanBehaviour.Update).</summary>
    public static class MainThread
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();

        public static void Post(Action a)
        {
            lock (Queue) Queue.Enqueue(a);
        }

        /// <summary>Blocks the calling (non-main) thread until the function has run on the main thread.</summary>
        public static T Invoke<T>(Func<T> f, int timeoutMs = 15000)
        {
            T result = default(T);
            Exception error = null;
            var done = new ManualResetEvent(false);
            Post(() =>
            {
                try { result = f(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            });
            if (!done.WaitOne(timeoutMs)) throw new TimeoutException("Main thread did not respond");
            if (error != null) throw new Exception(error.Message, error);
            return result;
        }

        public static void Drain()
        {
            while (true)
            {
                Action a;
                lock (Queue)
                {
                    if (Queue.Count == 0) return;
                    a = Queue.Dequeue();
                }
                try { a(); }
                catch (Exception e) { Log.Error("Main thread task failed: " + e); }
            }
        }
    }
}
