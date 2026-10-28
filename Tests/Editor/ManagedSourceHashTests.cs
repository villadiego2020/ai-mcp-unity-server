using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public sealed class ManagedSourceHashTests
    {
        [SetUp] public void WindowsOnly() { if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Native source SHA-256 uses Windows BCrypt."); }
        static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        static byte[] Data(int count) { var bytes = new byte[count]; for (int index = 0; index < count; index++) bytes[index] = (byte)((index * 31 + (index >> 8)) & 255); return bytes; }
        static byte[] Reference(byte[] bytes) { using (var sha = SHA256.Create()) return sha.ComputeHash(bytes); }
        static void Balanced(ManagedNativeHashCounters before, int count)
        {
            var after = ManagedSourceHash.Counters;
            Assert.That(after.opened - before.opened, Is.EqualTo(count)); Assert.That(after.created - before.created, Is.EqualTo(count));
            Assert.That(after.destroyAttempts - before.destroyAttempts, Is.EqualTo(count)); Assert.That(after.destroyed - before.destroyed, Is.EqualTo(count));
            Assert.That(after.closeAttempts - before.closeAttempts, Is.EqualTo(count)); Assert.That(after.closed - before.closed, Is.EqualTo(count));
            Assert.That(after.cleanupFailures - before.cleanupFailures, Is.Zero);
        }

        [TestCase("", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")]
        [TestCase("abc", "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")]
        public void NativeGoldenVectorsPreserveCanonicalSha256(string text, string expected)
        {
            var before = ManagedSourceHash.Counters;
            Assert.That(Hex(ManagedSourceHash.NativeDigest(Encoding.ASCII.GetBytes(text))), Is.EqualTo(expected)); Balanced(before, 1);
        }

        [TestCase(65535)]
        [TestCase(65536)]
        [TestCase(65537)]
        public void ThresholdAndPartialChunkProduceIdenticalExistingSourceIdentity(int length)
        {
            byte[] bytes = Data(length); string expected = Hex(Reference(bytes)); var before = ManagedSourceHash.Counters;
            Assert.That(ManagedSessionFiles.Hash(bytes), Is.EqualTo(expected)); Balanced(before, length >= ManagedSourceHash.NativeThresholdBytes ? 1 : 0);
            string path = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer-hash-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(path, bytes); before = ManagedSourceHash.Counters;
                Assert.That(ManagedSessionIntegration.HashFile(path, CancellationToken.None), Is.EqualTo(expected)); Balanced(before, length >= ManagedSourceHash.NativeThresholdBytes ? 1 : 0);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        sealed class ObservedStream : MemoryStream
        {
            internal int reads; internal bool disposed, failSecond, collectSecond; internal CancellationTokenSource cancelSecond;
            internal ObservedStream(byte[] bytes) : base(bytes) { }
            public override int Read(byte[] buffer, int offset, int count)
            {
                reads++;
                if (reads == 2)
                {
                    if (failSecond) throw new IOException("Controlled stream read failure");
                    if (collectSecond) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
                    cancelSecond?.Cancel();
                }
                return base.Read(buffer, offset, count);
            }
            protected override void Dispose(bool disposing) { disposed = true; base.Dispose(disposing); }
        }

        [Test]
        public void StreamChunksSurviveGcAndNeverDisposeBorrowedStream()
        {
            byte[] bytes = Data(2 * 65536 + 19); var before = ManagedSourceHash.Counters;
            using (var stream = new ObservedStream(bytes) { collectSecond = true })
            {
                Assert.That(ManagedSourceHash.NativeDigest(stream, CancellationToken.None), Is.EqualTo(Reference(bytes)));
                Assert.That(stream.Position, Is.EqualTo(bytes.Length)); Assert.That(stream.disposed, Is.False); Assert.That(stream.reads, Is.EqualTo(4));
            }
            Balanced(before, 1);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ReadFailureOrCancellationClosesOnlyOwnedNativeHandles(bool cancelled)
        {
            var before = ManagedSourceHash.Counters;
            using (var source = new CancellationTokenSource())
            using (var stream = new ObservedStream(Data(3 * 65536 + 7)) { failSecond = !cancelled, cancelSecond = cancelled ? source : null })
            {
                if (cancelled) Assert.Throws<OperationCanceledException>(() => ManagedSourceHash.NativeDigest(stream, source.Token));
                else Assert.Throws<IOException>(() => ManagedSourceHash.NativeDigest(stream, source.Token));
                Assert.That(stream.disposed, Is.False); Assert.That(stream.CanRead, Is.True);
            }
            Balanced(before, 1);
        }

        [Test]
        public void FailedFinishIsExplicitAndStillDestroysHashThenClosesProvider()
        {
            var before = ManagedSourceHash.Counters;
            var method = typeof(ManagedSourceHash).GetMethod("NativeBufferDigest", BindingFlags.Static | BindingFlags.NonPublic);
            var error = Assert.Throws<TargetInvocationException>(() => method.Invoke(null, new object[] { Encoding.ASCII.GetBytes("abc"), 31 }));
            Assert.That(error.InnerException, Is.TypeOf<CryptographicException>()); StringAssert.Contains("NTSTATUS", error.InnerException.Message); Balanced(before, 1);
        }

        [Test]
        public void ConcurrentCallsOwnIndependentHandlesAndKeepIdenticalDigests()
        {
            var before = ManagedSourceHash.Counters;
            Parallel.For(0, 32, index =>
            {
                byte[] bytes = Data(65536 + index);
                if (Hex(ManagedSourceHash.NativeDigest(bytes)) != Hex(Reference(bytes))) throw new InvalidOperationException("Concurrent SHA-256 mismatch.");
                if (index % 8 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            });
            Balanced(before, 32);
        }
    }
}
