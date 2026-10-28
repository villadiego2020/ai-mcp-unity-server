using System;

namespace AIUnityMCPServer.Runtime
{
    internal sealed class ManagedSupervisorLease
    {
        internal const long TimeoutMilliseconds = 90000;
        long lastValidStamp;

        internal ManagedSupervisorLease(long startedAt) { lastValidStamp = startedAt; }
        internal void Observe(long? timestamp)
        {
            if (timestamp.HasValue && timestamp.Value > 0) lastValidStamp = Math.Max(lastValidStamp, timestamp.Value);
        }
        internal bool IsExpired(long now) => now - lastValidStamp > TimeoutMilliseconds;
    }
}
