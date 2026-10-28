using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace AIUnityMCPServer
{
    // Owns pending work only. Running Unity operations cannot be safely interrupted.
    internal sealed class MCPRequestQueue
    {
        internal sealed class Request
        {
            internal Func<string> Action;
            internal LinkedListNode<Request> Node;
            internal long Deadline;
            internal bool Running;
            internal bool Completed;
            internal string Result;
        }

        const string Expired = "{\"error\":\"Unity was busy and the queued request expired before execution. No action was started.\",\"code\":\"QUEUE_TIMEOUT\",\"retryable\":true}";
        readonly object gate = new object();
        readonly LinkedList<Request> pending = new LinkedList<Request>();
        readonly int capacity;
        readonly int queueTimeoutMilliseconds;
        readonly Func<long> clock;

        internal MCPRequestQueue(int capacity, int queueTimeoutMilliseconds, Func<long> clock = null)
        {
            if (capacity < 1 || queueTimeoutMilliseconds < 1) throw new ArgumentOutOfRangeException();
            this.capacity = capacity;
            this.queueTimeoutMilliseconds = queueTimeoutMilliseconds;
            this.clock = clock ?? (() => (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)));
        }

        internal int PendingCount { get { lock (gate) return pending.Count; } }

        internal bool TryEnqueue(Func<string> action, out Request request)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            lock (gate)
            {
                RemoveExpired();
                if (pending.Count >= capacity) { request = null; return false; }
                request = new Request { Action = action, Deadline = clock() + queueTimeoutMilliseconds };
                request.Node = pending.AddLast(request);
                return true;
            }
        }

        internal string Execute(Func<string> action, int waitMilliseconds)
        {
            if (!TryEnqueue(action, out Request request))
                return "{\"error\":\"Unity request queue is full. Retry after pending work completes.\",\"code\":\"QUEUE_FULL\",\"retryable\":true}";
            return Wait(request, waitMilliseconds);
        }

        internal string Wait(Request request, int waitMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            lock (gate)
            {
                while (!request.Completed)
                {
                    int remaining = waitMilliseconds - (int)Math.Min(int.MaxValue, elapsed.ElapsedMilliseconds);
                    if (!request.Running)
                        remaining = (int)Math.Min(remaining, Math.Max(0, request.Deadline - clock()));
                    if (remaining <= 0)
                    {
                        if (!request.Running)
                        {
                            pending.Remove(request.Node);
                            Complete(request, Expired);
                            break;
                        }
                        return "{\"error\":\"Unity execution has already started and its outcome is not yet known. Inspect Unity state before retrying; do not replay this operation automatically.\",\"code\":\"EXECUTION_IN_PROGRESS\",\"retryable\":false}";
                    }
                    Monitor.Wait(gate, remaining);
                }
                return request.Result;
            }
        }

        // The budget is cooperative: an individual Unity operation cannot be preempted.
        internal void Pump(int maximumJobs, int maximumMilliseconds)
        {
            var elapsed = Stopwatch.StartNew();
            for (int count = 0; count < maximumJobs && elapsed.ElapsedMilliseconds < maximumMilliseconds; count++)
            {
                Request request;
                lock (gate)
                {
                    RemoveExpired();
                    if (pending.Count == 0) return;
                    request = pending.First.Value;
                    pending.RemoveFirst();
                    request.Node = null;
                    request.Running = true;
                }
                string result;
                try { result = request.Action() ?? "{\"error\":\"No result\"}"; }
                catch (Exception exception)
                {
                    string message = exception.Message.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
                    result = "{\"error\":\"" + message + "\"}";
                }
                lock (gate) Complete(request, result);
            }
        }

        internal void CancelPending()
        {
            lock (gate)
            {
                while (pending.Count > 0)
                {
                    Request request = pending.First.Value;
                    pending.RemoveFirst();
                    Complete(request, "{\"error\":\"Unity is reloading or exiting. The queued request was not started.\",\"code\":\"EDITOR_RELOADING\",\"retryable\":true}");
                }
            }
        }

        void RemoveExpired()
        {
            long now = clock();
            while (pending.First != null && pending.First.Value.Deadline <= now)
            {
                Request request = pending.First.Value;
                pending.RemoveFirst();
                Complete(request, Expired);
            }
        }

        void Complete(Request request, string result)
        {
            request.Node = null;
            request.Action = null;
            request.Result = result;
            request.Completed = true;
            Monitor.PulseAll(gate);
        }
    }
}
