using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace AIUnityMCPServer
{
    internal struct ManagedNativeHashCounters
    { internal long opened, created, destroyed, closed, destroyAttempts, closeAttempts, cleanupFailures; }

    // SHA-256 is unchanged. Large Windows source buffers use the documented CNG
    // implementation because Unity Mono's managed implementation stalls Main commits.
    internal static class ManagedSourceHash
    {
        internal const int NativeThresholdBytes = 65536;
        static long opened, created, destroyed, closed, destroyAttempts, closeAttempts, cleanupFailures;
        internal static bool UseNative(long bytes) => Environment.OSVersion.Platform == PlatformID.Win32NT && bytes >= NativeThresholdBytes;
        internal static ManagedNativeHashCounters Counters => new ManagedNativeHashCounters
        {
            opened = Interlocked.Read(ref opened), created = Interlocked.Read(ref created), destroyed = Interlocked.Read(ref destroyed), closed = Interlocked.Read(ref closed),
            destroyAttempts = Interlocked.Read(ref destroyAttempts), closeAttempts = Interlocked.Read(ref closeAttempts), cleanupFailures = Interlocked.Read(ref cleanupFailures)
        };

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string algorithmId, string implementation, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        static extern int BCryptCreateHash(IntPtr algorithm, out IntPtr hash, IntPtr hashObject, uint hashObjectBytes, IntPtr secret, uint secretBytes, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        static extern int BCryptHashData(IntPtr hash, [In] byte[] input, uint bytes, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        static extern int BCryptFinishHash(IntPtr hash, [Out] byte[] output, uint bytes, uint flags);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        static extern int BCryptDestroyHash(IntPtr hash);
        [DllImport("bcrypt.dll", ExactSpelling = true)]
        static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, uint flags);

        internal static byte[] NativeDigest(byte[] input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            return NativeBufferDigest(input, 32);
        }
        static byte[] NativeBufferDigest(byte[] input, int outputBytes) => Compute(hash => Check(BCryptHashData(hash, input, (uint)input.Length, 0), "BCryptHashData"), outputBytes);

        internal static byte[] NativeDigest(Stream input, CancellationToken cancellation)
        {
            if (input == null || !input.CanRead) throw new ArgumentException("A readable caller-owned stream is required.", nameof(input));
            return Compute(hash =>
            {
                var buffer = new byte[NativeThresholdBytes];
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int count = input.Read(buffer, 0, buffer.Length);
                    if (count == 0) break;
                    Check(BCryptHashData(hash, buffer, (uint)count, 0), "BCryptHashData");
                }
            }, 32);
        }

        static byte[] Compute(Action<IntPtr> append, int outputBytes)
        {
            IntPtr algorithm = IntPtr.Zero, hash = IntPtr.Zero;
            Exception failure = null; var cleanup = new List<Exception>(); var digest = new byte[outputBytes];
            try
            {
                Check(BCryptOpenAlgorithmProvider(out IntPtr acquiredAlgorithm, "SHA256", null, 0), "BCryptOpenAlgorithmProvider");
                algorithm = acquiredAlgorithm;
                if (algorithm == IntPtr.Zero) throw new CryptographicException("BCrypt returned no SHA-256 provider.");
                Interlocked.Increment(ref opened);
                // Windows 7+ owns the hash object buffer when NULL/0 is supplied; DestroyHash
                // releases it. No managed array address is retained across calls or GC.
                Check(BCryptCreateHash(algorithm, out IntPtr acquiredHash, IntPtr.Zero, 0, IntPtr.Zero, 0, 0), "BCryptCreateHash");
                hash = acquiredHash;
                if (hash == IntPtr.Zero) throw new CryptographicException("BCrypt returned no SHA-256 hash object.");
                Interlocked.Increment(ref created);
                append(hash);
                Check(BCryptFinishHash(hash, digest, (uint)digest.Length, 0), "BCryptFinishHash");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                // Each locally acquired handle is released exactly once, including failed
                // reads/cancellation/FinishHash. Provider cleanup still runs if Destroy fails.
                if (hash != IntPtr.Zero)
                {
                    Interlocked.Increment(ref destroyAttempts);
                    try { Check(BCryptDestroyHash(hash), "BCryptDestroyHash"); Interlocked.Increment(ref destroyed); }
                    catch (Exception error) { Interlocked.Increment(ref cleanupFailures); cleanup.Add(error); }
                    hash = IntPtr.Zero;
                }
                if (algorithm != IntPtr.Zero)
                {
                    Interlocked.Increment(ref closeAttempts);
                    try { Check(BCryptCloseAlgorithmProvider(algorithm, 0), "BCryptCloseAlgorithmProvider"); Interlocked.Increment(ref closed); }
                    catch (Exception error) { Interlocked.Increment(ref cleanupFailures); cleanup.Add(error); }
                    algorithm = IntPtr.Zero;
                }
            }
            if (failure != null && cleanup.Count != 0) { cleanup.Insert(0, failure); throw new AggregateException("SHA-256 operation and cleanup failed.", cleanup); }
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            if (cleanup.Count != 0) throw new AggregateException("SHA-256 cleanup failed.", cleanup);
            return digest;
        }

        static void Check(int status, string operation)
        { if (status < 0) throw new CryptographicException(operation + " failed with NTSTATUS 0x" + unchecked((uint)status).ToString("X8") + "."); }
    }
}
