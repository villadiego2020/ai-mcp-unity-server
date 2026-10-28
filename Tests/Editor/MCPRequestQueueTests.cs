using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public class MCPRequestQueueTests
    {
        [Test]
        public void TimedOutPendingRequestCannotExecuteLater()
        {
            var queue = new MCPRequestQueue(2, 20000);
            int writes = 0;
            Assert.That(queue.TryEnqueue(() => { writes++; return "ok"; }, out var request), Is.True);
            StringAssert.Contains("QUEUE_TIMEOUT", queue.Wait(request, 0));
            queue.Pump(4, 1000);
            Assert.That(writes, Is.Zero);
            Assert.That(queue.PendingCount, Is.Zero);
        }

        [Test]
        public void QueueIsBoundedFifoAndPumpHasAnItemBudget()
        {
            var queue = new MCPRequestQueue(3, 20000);
            var observed = new List<int>();
            for (int index = 0; index < 3; index++)
            {
                int value = index;
                Assert.That(queue.TryEnqueue(() => { observed.Add(value); return "ok"; }, out _), Is.True);
            }
            StringAssert.Contains("QUEUE_FULL", queue.Execute(() => "unexpected", 0));
            queue.Pump(2, 1000);
            Assert.That(observed, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(queue.PendingCount, Is.EqualTo(1));
            queue.Pump(2, 1000);
            Assert.That(observed, Is.EqualTo(new[] { 0, 1, 2 }));
        }

        [Test]
        public void ExpiredRequestsReleaseCapacityAndAreNeverStarted()
        {
            long now = 0;
            var queue = new MCPRequestQueue(1, 10, () => now);
            int writes = 0;
            queue.TryEnqueue(() => { writes++; return "old"; }, out var expired);
            now = 11;
            Assert.That(queue.TryEnqueue(() => "new", out var current), Is.True);
            queue.Pump(1, 1000);
            StringAssert.Contains("QUEUE_TIMEOUT", queue.Wait(expired, 0));
            Assert.That(queue.Wait(current, 0), Is.EqualTo("new"));
            Assert.That(writes, Is.Zero);
        }

        [Test]
        public void RunningRequestReturnsUnknownOutcomeAndCompletesOnlyOnce()
        {
            var queue = new MCPRequestQueue(1, 20000);
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                int writes = 0;
                queue.TryEnqueue(() =>
                {
                    started.Set();
                    if (!release.Wait(2000)) throw new TimeoutException("Test release was not signalled.");
                    writes++;
                    return "finished";
                }, out var request);
                var worker = new Thread(() => queue.Pump(1, 1000));
                worker.Start();
                try
                {
                    Assert.That(started.Wait(2000), Is.True);
                    StringAssert.Contains("EXECUTION_IN_PROGRESS", queue.Wait(request, 0));
                    Assert.That(writes, Is.Zero);
                }
                finally { release.Set(); worker.Join(3000); }
                Assert.That(queue.Wait(request, 0), Is.EqualTo("finished"));
                queue.Pump(1, 1000);
                Assert.That(writes, Is.EqualTo(1));
            }
        }

        [Test]
        public void ReloadCancellationWakesRequestsWithoutStartingThem()
        {
            var queue = new MCPRequestQueue(2, 20000);
            int writes = 0;
            queue.TryEnqueue(() => { writes++; return "bad"; }, out var request);
            queue.CancelPending();
            StringAssert.Contains("EDITOR_RELOADING", queue.Wait(request, 0));
            queue.Pump(1, 1000);
            Assert.That(writes, Is.Zero);
            Assert.That(queue.PendingCount, Is.Zero);
        }

        [Test]
        public void FailedOperationDoesNotBlockFollowingWork()
        {
            var queue = new MCPRequestQueue(2, 20000);
            queue.TryEnqueue(() => throw new InvalidOperationException("failed"), out var failed);
            queue.TryEnqueue(() => "next", out var next);
            queue.Pump(2, 1000);
            StringAssert.Contains("failed", queue.Wait(failed, 0));
            Assert.That(queue.Wait(next, 0), Is.EqualTo("next"));
        }

        [Test]
        public void QueuedMutationRechecksWritePermissionBeforeExecution()
        {
            bool previous = MCPHandlers.AllowWrites;
            var queue = (MCPRequestQueue)typeof(MCPHandlers).GetField("_mainQueue", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            string result = null;
            var request = new Thread(() => result = MCPHandlers.DispatchFrom("QueueTest", "/scene/save", "{}"));
            try
            {
                MCPHandlers.AllowWrites = true;
                request.Start();
                Assert.That(SpinWait.SpinUntil(() => queue.PendingCount > 0, 2000), Is.True);
                MCPHandlers.AllowWrites = false;
                queue.Pump(4, 1000);
                Assert.That(request.Join(2000), Is.True);
                StringAssert.Contains("READ-ONLY", result);
            }
            finally
            {
                queue.CancelPending();
                request.Join(2000);
                MCPHandlers.AllowWrites = previous;
            }
        }

        [Test]
        public void SuccessfulPingsCoalesceOnlyWithinSameSourceAndWithoutInterveningCommands()
        {
            var original = new List<MCPHandlers.MCPLogEntry>(MCPHandlers.Log);
            try
            {
                MCPHandlers.Log.Clear();
                MCPHandlers.DispatchFrom("QueueTestA", "/ping", "{}");
                MCPHandlers.DispatchFrom("QueueTestA", "/ping", "{}");
                Assert.That(MCPHandlers.Log.Count, Is.EqualTo(1));
                Assert.That(MCPHandlers.Log[0].RepeatCount, Is.EqualTo(2));
                MCPHandlers.DispatchFrom("QueueTestB", "/ping", "{}");
                Assert.That(MCPHandlers.Log.Count, Is.EqualTo(2));
                MCPHandlers.Dispatch("/not-a-command", "{}", false);
                MCPHandlers.DispatchFrom("QueueTestB", "/ping", "{}");
                Assert.That(MCPHandlers.Log.Count, Is.EqualTo(4));
                Assert.That(MCPHandlers.Log[2].IsError, Is.True);
            }
            finally
            {
                MCPHandlers.Log.Clear();
                MCPHandlers.Log.AddRange(original);
            }
        }
    }
}
