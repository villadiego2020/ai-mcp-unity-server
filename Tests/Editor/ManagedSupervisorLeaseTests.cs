using AIUnityMCPServer.Runtime;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public sealed class ManagedSupervisorLeaseTests
    {
        [Test]
        public void MissingAndInvalidReadDoNotBypassExistingGrace()
        {
            var lease = new ManagedSupervisorLease(1000);
            lease.Observe(null);
            lease.Observe(0);
            lease.Observe(-1);
            Assert.That(lease.IsExpired(91000), Is.False);
            Assert.That(lease.IsExpired(91001), Is.True);
        }

        [Test]
        public void ResumedValidHeartbeatRenewsWithoutOlderReadRollingItBack()
        {
            var lease = new ManagedSupervisorLease(1000);
            lease.Observe(90000);
            lease.Observe(null);
            lease.Observe(50000);
            Assert.That(lease.IsExpired(180000), Is.False);
            Assert.That(lease.IsExpired(180001), Is.True);
        }

        [Test]
        public void FutureReloadHeartbeatPreservesAdditionalSixtySeconds()
        {
            var lease = new ManagedSupervisorLease(1000);
            lease.Observe(61000);
            lease.Observe(2000);
            Assert.That(lease.IsExpired(151000), Is.False);
            Assert.That(lease.IsExpired(151001), Is.True);
        }
    }
}
