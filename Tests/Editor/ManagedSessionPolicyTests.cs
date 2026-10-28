using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public sealed class ManagedSessionPolicyTests
    {
        string root;
        [SetUp] public void SetUp() { root = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer-policy-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        string Project(string name)
        {
            string project = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(project, "Assets"));
            Directory.CreateDirectory(Path.Combine(project, "Packages"));
            Directory.CreateDirectory(Path.Combine(project, "ProjectSettings"));
            File.WriteAllText(Path.Combine(project, "Assets", "Marker.txt"), "main");
            File.WriteAllText(Path.Combine(project, "Packages", "manifest.json"), "{\"dependencies\":{}}");
            return project;
        }

        [TestCase("Assets/../outside.txt")]
        [TestCase("Assets/.env")]
        [TestCase("Assets/credentials.json")]
        [TestCase("Packages/manifest.json")]
        [TestCase("C:/outside.txt")]
        public void WorkspacePathsRejectTraversalSecretsAndNonAssets(string relative)
        { Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.AssetPath(Project("main"), relative)); }

        [Test]
        public void PrivateSourceSnapshotsDoNotShareWritableAssetsWithMainOrEachOther()
        {
            string main = Project("main");
            string a = Path.Combine(root, "A"); string b = Path.Combine(root, "B");
            ManagedSessionFiles.Snapshot(main, a, true, CancellationToken.None);
            ManagedSessionFiles.Snapshot(main, b, true, CancellationToken.None);
            File.WriteAllText(Path.Combine(a, "Assets", "Marker.txt"), "A");
            Assert.That(File.ReadAllText(Path.Combine(main, "Assets", "Marker.txt")), Is.EqualTo("main"));
            Assert.That(File.ReadAllText(Path.Combine(b, "Assets", "Marker.txt")), Is.EqualTo("main"));
            Assert.That(ManagedSessionFiles.ProjectFingerprint(a, CancellationToken.None), Is.Not.EqualTo(ManagedSessionFiles.ProjectFingerprint(b, CancellationToken.None)));
        }

        [Test]
        public void LocalPackagesAreCopiedPrivatelyAndManifestPointsOnlyAtPrivateVendor()
        {
            string main = Project("main"); string package = Path.Combine(root, "package");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.example.local\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(package, "Marker.txt"), "original");
            File.WriteAllText(Path.Combine(main, "Packages", "manifest.json"), new JObject { ["dependencies"] = new JObject { ["com.example.local"] = "file:" + package.Replace('\\', '/') } }.ToString());
            string copy = Path.Combine(root, "copy");
            ManagedSessionFiles.Snapshot(main, copy, true, CancellationToken.None);
            Assert.That(JObject.Parse(File.ReadAllText(Path.Combine(copy, "Packages", "manifest.json")))["dependencies"]["com.example.local"].ToString(), Is.EqualTo("file:../ManagedVendor/com.example.local"));
            File.WriteAllText(Path.Combine(copy, "ManagedVendor", "com.example.local", "Marker.txt"), "private");
            Assert.That(File.ReadAllText(Path.Combine(package, "Marker.txt")), Is.EqualTo("original"));
        }

        [Test]
        public void TransitiveLocalDependenciesAreRejectedInsteadOfSharingSource()
        {
            string main = Project("main"); string package = Path.Combine(root, "package");
            Directory.CreateDirectory(package);
            File.WriteAllText(Path.Combine(package, "package.json"), "{\"name\":\"com.example.local\",\"dependencies\":{\"com.example.other\":\"file:../other\"}}");
            File.WriteAllText(Path.Combine(main, "Packages", "manifest.json"), new JObject { ["dependencies"] = new JObject { ["com.example.local"] = "file:" + package.Replace('\\', '/') } }.ToString());
            var error = Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.Snapshot(main, Path.Combine(root, "copy"), true, CancellationToken.None));
            StringAssert.Contains("LOCAL_DEPENDENCY_UNSUPPORTED", error.Message);
        }

        [Test]
        public void SnapshotsExcludeTransientAndKnownSecretFiles()
        {
            string main = Project("main");
            File.WriteAllText(Path.Combine(main, "Assets", ".env"), "excluded");
            Directory.CreateDirectory(Path.Combine(main, "Assets", "node_modules"));
            File.WriteAllText(Path.Combine(main, "Assets", "node_modules", "unwanted.txt"), "excluded");
            Directory.CreateDirectory(Path.Combine(main, "Library"));
            File.WriteAllText(Path.Combine(main, "Library", "unwanted.txt"), "excluded");
            string copy = Path.Combine(root, "copy");
            ManagedSessionFiles.Snapshot(main, copy, true, CancellationToken.None);
            Assert.That(File.Exists(Path.Combine(copy, "Assets", ".env")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(copy, "Assets", "node_modules")), Is.False);
            Assert.That(Directory.Exists(Path.Combine(copy, "Library")), Is.False);
        }

        [Test]
        public void CapabilityVerificationRejectsAnotherOwnersToken()
        {
            string a = ManagedSessionFiles.NewCapability(), b = ManagedSessionFiles.NewCapability();
            Assert.That(a, Has.Length.EqualTo(64));
            Assert.That(ManagedSessionFiles.CapabilityMatches(a, ManagedSessionFiles.HashText(a)), Is.True);
            Assert.That(ManagedSessionFiles.CapabilityMatches(b, ManagedSessionFiles.HashText(a)), Is.False);
            Assert.That(ManagedSessionFiles.CapabilityMatches(null, ManagedSessionFiles.HashText(a)), Is.False);
        }

        [Test]
        public void ManagedWriteGateRequiresAuthorityButAllowsOwnerCleanupWhenDisabled()
        {
            Assert.That(ManagedSessionManager.RequiresWrites("session", "register"), Is.True);
            Assert.That(ManagedSessionManager.RequiresWrites("workspace", "write"), Is.True);
            Assert.That(ManagedSessionManager.RequiresWrites("build", "start"), Is.True);
            Assert.That(ManagedSessionManager.RequiresWrites("player", "input"), Is.True);
            Assert.That(ManagedSessionManager.RequiresWrites("player", "close"), Is.False);
            Assert.That(ManagedSessionManager.RequiresWrites("session", "events"), Is.False);
        }

        [Test]
        public void ProcessOwnershipRequiresLivePidStartTimeAndExactExecutablePath()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Managed process ownership is Windows-only.");
            string executable = Environment.GetEnvironmentVariable("COMSPEC");
            Assert.That(File.Exists(executable), Is.True, "Windows command interpreter is required for this isolated child-process check.");
            var info = new System.Diagnostics.ProcessStartInfo(executable, "/d /c pause >nul")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            using (var process = System.Diagnostics.Process.Start(info))
            {
                try
                {
                    int pid = process.Id;
                    long start = process.StartTime.ToUniversalTime().Ticks;
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(pid, start, executable), Is.True);
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(pid, start + 1, executable), Is.False);
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(pid, start, Path.Combine(root, Path.GetFileName(executable))), Is.False);
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(pid, start, null), Is.False);
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(int.MaxValue, start, executable), Is.False);
                    process.StandardInput.WriteLine();
                    Assert.That(process.WaitForExit(5000), Is.True, "The task-owned command interpreter must exit after its input.");
                    Assert.That(ManagedSessionManager.IsOwnedProcessAlive(pid, start, executable), Is.False);
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
                }
            }
        }

        [Test]
        public void ImageQueriesRejectInvalidOrUnavailableIdentityWithoutFallback()
        {
            Assert.That(ManagedProcessImage.Matches(0, "unused.exe"), Is.False);
            Assert.That(ManagedProcessImage.Matches(-1, "unused.exe"), Is.False);
            Assert.That(ManagedProcessImage.Matches(int.MaxValue, "unused.exe"), Is.False);
            Assert.That(ManagedProcessImage.Matches(1, null), Is.False);
        }

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool GetHandleInformation(IntPtr handle, out uint flags);

        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [Test]
        public void ListenerInheritanceGuardClearsNativeFlagAndPreservesSocketOwnership()
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Socket inheritance flags are Windows-only.");
            using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
            {
                IntPtr handle = socket.Handle;
                Assert.That(SetHandleInformation(handle, 1, 1), Is.True, "Establish an inheritable socket for the regression check.");
                Assert.That(GetHandleInformation(handle, out uint before), Is.True);
                Assert.That(before & 1, Is.EqualTo(1));
                MCPListenerSocket.PreventInheritance(socket);
                Assert.That(GetHandleInformation(handle, out uint after), Is.True);
                Assert.That(after & 1, Is.Zero);
                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
                socket.Listen(1);
                Assert.That(socket.IsBound, Is.True, "The guard must leave the Socket able to own and use its handle.");
            }
        }

        [Test]
        public void LegitimateSecretDoorSourceIsCopiedAndKnownSensitiveFilesAreReported()
        {
            string main = Project("main");
            File.WriteAllText(Path.Combine(main, "Assets", "SecretDoor.cs"), "// legitimate source");
            File.WriteAllText(Path.Combine(main, "Assets", "credentials.json"), "excluded");
            string copy = Path.Combine(root, "copy");
            ManagedSessionFiles.Snapshot(main, copy, true, CancellationToken.None);
            Assert.That(File.Exists(Path.Combine(copy, "Assets", "SecretDoor.cs")), Is.True);
            var report = JObject.Parse(File.ReadAllText(Path.Combine(copy, "managed-snapshot-report.json")));
            Assert.That(report["excludedPaths"].Values<string>(), Does.Contain("Assets/credentials.json"));
        }

        [Test]
        public void LockedAtomicReplacementFailsBoundedlyPreservesOriginalAndCleansTemporaryFiles()
        {
            string path = Path.Combine(root, "state.json");
            File.WriteAllText(path, "original");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Assert.Throws<IOException>(() => ManagedSessionFiles.WriteAtomic(path, "replacement"));
                Assert.That(timer.ElapsedMilliseconds, Is.LessThan(2000));
                Assert.That(Directory.EnumerateFiles(root, "*.tmp"), Is.Empty);
            }
            Assert.That(File.ReadAllText(path), Is.EqualTo("original"));
            ManagedSessionFiles.WriteAtomic(path, "after-release");
            Assert.That(File.ReadAllText(path), Is.EqualTo("after-release"));
        }

        [Test]
        public void SchemaValidUnicodeInputCannotExceedSerializedMailboxByteBudget()
        {
            string project = Project("main");
            var manager = new ManagedSessionManager(project, "unused", "test", Path.Combine(root, "storage"));
            var state = (ManagedSessionState)typeof(ManagedSessionManager).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            string capability = ManagedSessionFiles.NewCapability();
            state.sessions.Add(new ManagedSessionRecord { id = "session-test", state = "ready", workspace = project, capabilityHash = ManagedSessionFiles.HashText(capability) });
            string mailbox = Path.Combine(root, "mailbox"); Directory.CreateDirectory(mailbox);
            state.runs.Add(new ManagedRunRecord { id = "run-test", sessionId = "session-test", state = "running", mailbox = mailbox, capability = ManagedSessionFiles.NewCapability() });
            var response = manager.Execute("player", new JObject { ["action"] = "input", ["sessionId"] = "session-test", ["capability"] = capability,
                ["runId"] = "run-test", ["requestId"] = "unicode-input", ["inputName"] = "state", ["inputValue"] = new string('\u4e00', 32768) });
            Assert.That(response.Value<string>("code"), Is.EqualTo("COMMAND_LIMIT"));
            Assert.That(Directory.EnumerateFiles(mailbox, "*.request.json"), Is.Empty);
        }

        [Test]
        public void PlayerStatusSurvivesLockedMissingMalformedTelemetryAndRecovers()
        {
            string project = Project("main");
            var manager = new ManagedSessionManager(project, "unused", "test", Path.Combine(root, "storage"));
            var state = (ManagedSessionState)typeof(ManagedSessionManager).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
            string capability = ManagedSessionFiles.NewCapability();
            state.sessions.Add(new ManagedSessionRecord { id = "session-test", state = "ready", workspace = project, capabilityHash = ManagedSessionFiles.HashText(capability) });
            string mailbox = Path.Combine(root, "mailbox"); Directory.CreateDirectory(mailbox);
            state.runs.Add(new ManagedRunRecord { id = "run-test", sessionId = "session-test", state = "running", mailbox = mailbox });
            var request = new JObject { ["action"] = "status", ["sessionId"] = "session-test", ["capability"] = capability, ["runId"] = "run-test" };
            string heartbeat = Path.Combine(mailbox, "player-heartbeat");
            File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            using (var locked = new FileStream(heartbeat, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var result = manager.Execute("player", request);
                Assert.That(result["error"], Is.Null);
                Assert.That(result["runs"][0].Value<string>("state"), Is.EqualTo("running"));
                Assert.That(result["runs"][0].Value<string>("heartbeatStatus"), Is.EqualTo("temporarily_unavailable"));
            }
            File.WriteAllText(heartbeat, "invalid");
            Assert.That(manager.Execute("player", request)["runs"][0].Value<string>("heartbeatDetail"), Is.EqualTo("invalid_timestamp"));
            File.Delete(heartbeat);
            Assert.That(manager.Execute("player", request)["runs"][0].Value<string>("heartbeatStatus"), Is.EqualTo("temporarily_unavailable"));
            File.WriteAllText(heartbeat, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
            Assert.That(manager.Execute("player", request)["runs"][0].Value<string>("heartbeatStatus"), Is.EqualTo("available"));
        }

        [Test]
        public void LargeSavedSourceFitsPolicyAndEachCapacityBoundaryRemainsBounded()
        {
            const long gib = 1024L * 1024 * 1024;
            Assert.DoesNotThrow(() => ManagedSessionFiles.ValidateSnapshotMetrics(8 * gib, 80L * 1024 * 1024, 44787));
            Assert.DoesNotThrow(() => ManagedSessionFiles.ValidateSnapshotMetrics(16 * gib, 256L * 1024 * 1024, 50000));
            Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.ValidateSnapshotMetrics(16 * gib + 1, 1, 1));
            Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.ValidateSnapshotMetrics(1, 256L * 1024 * 1024 + 1, 1));
            Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.ValidateSnapshotMetrics(1, 1, 50001));
            Assert.That(ManagedSessionFiles.MaximumRetainedBytes, Is.EqualTo(64 * gib));
        }

        [TestCase("6000.5.10f1", false)]
        [TestCase("6000.5.11f1", false)]
        [TestCase("6000.0.74f1", false)]
        [TestCase("6000.0.76f1", false)]
        [TestCase("6000.0.75F1", false)]
        [TestCase(null, false)]
        [TestCase("", false)]
        [TestCase("6000.0.75f1", true)]
        public void QualificationAllowsOnlyTheExactVerifiedVersion(string version, bool qualified)
        {
            Assert.That(ManagedSessionQualification.ExclusionReason(version) == null, Is.EqualTo(qualified));
        }

        [Test]
        public void ExplicitBatchSupervisorRequiresBothBatchModeAndExplicitFlag()
        {
            Assert.That(ManagedSessionQualification.IsExplicitSupervisor(false, new[] { "-aiMcpManagedSupervisor" }), Is.False);
            Assert.That(ManagedSessionQualification.IsExplicitSupervisor(true, new[] { "-batchmode" }), Is.False);
            Assert.That(ManagedSessionQualification.IsExplicitSupervisor(true, new[] { "-batchmode", "-aiMcpManagedSupervisor" }), Is.True);
        }

        [Test]
        public void InteractiveEditorsAreEligibleWhileOrdinaryBatchWorkersRemainInert()
        {
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(false, null), Is.True);
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(false, new string[0]), Is.True);
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(true, null), Is.False);
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(true, new[] { "-batchmode", "-executeMethod", "AIUnityMCPServer.ManagedPlayerBuildWorker.Run" }), Is.False);
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(true, new[] { "-batchmode", "-aiMcpManagedSupervisor" }), Is.True);
            Assert.That(ManagedSessionQualification.IsEligibleHostMode(true, new[] { "-batchmode", "-aiMcpManagedSupervisor=true" }), Is.False);
        }
    }
}
