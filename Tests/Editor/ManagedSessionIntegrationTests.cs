using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public sealed class ManagedSessionIntegrationTests
    {
        string root, main, workspace, sidecar;
        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer-integration-" + Guid.NewGuid().ToString("N"));
            main = Path.Combine(root, "main"); workspace = Path.Combine(root, "private"); sidecar = Path.Combine(root, "sidecar");
            Directory.CreateDirectory(Path.Combine(main, "Assets")); Directory.CreateDirectory(Path.Combine(main, "Packages")); Directory.CreateDirectory(Path.Combine(main, "ProjectSettings"));
            File.WriteAllText(Path.Combine(main, "Packages", "manifest.json"), "{\"dependencies\":{}}");
        }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        static string Asset(string project, string name) => Path.Combine(project, "Assets", name);
        static void Put(string project, string name, byte[] bytes, string guid = null)
        {
            string path = Asset(project, name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
            File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + (guid ?? Guid.NewGuid().ToString("N")) + "\n");
        }
        static void Put(string project, string name, string text, string guid = null) => Put(project, name, Encoding.UTF8.GetBytes(text), guid);
        static void Remove(string project, string name) { File.Delete(Asset(project, name)); File.Delete(Asset(project, name) + ".meta"); }
        ManagedSnapshotResult Snapshot() => ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None);
        ManagedIntegrationPlan Plan(string baseline, string name = "plan")
        {
            string directory = Path.Combine(sidecar, name);
            string hash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baseline, directory, name, "session-test", CancellationToken.None);
            return ManagedSessionIntegration.ReadPlan(directory, hash);
        }
        static ManagedIntegrationChange Change(ManagedIntegrationPlan plan, string name) => plan.entries.Single(item => item.path == "Assets/" + name);
        static string Fingerprint(string project) => ManagedSessionFiles.ProjectFingerprint(project, CancellationToken.None);
        static void AssertNativeSuccessContract(JObject response)
        {
            Assert.That(response.Property("error"), Is.Null, "NativeCommandTool rejects any root error property, including JSON null: " + response);
            Assert.That(response.Property("errorCode"), Is.Null, response.ToString());
            Assert.That(JObject.Parse(response.ToString()).Property("error"), Is.Null, "The serialized transport response must also omit the root error field.");
        }

        [Test]
        public void PreviewClassifiesThreeWayBinaryChangesWithoutWritingMainAndPinsPayload()
        {
            foreach (string name in new[] { "Changed.bin", "Deleted.bin", "Conflict.bin", "Applied.bin", "MainOnly.bin" }) Put(main, name, new byte[] { 0, 255, 1 });
            var snapshot = Snapshot();
            File.WriteAllBytes(Asset(workspace, "Changed.bin"), new byte[] { 255, 0, 128 });
            Remove(workspace, "Deleted.bin"); Put(workspace, "Added.bin", new byte[] { 128, 129 });
            File.WriteAllBytes(Asset(workspace, "Conflict.bin"), new byte[] { 2 }); File.WriteAllBytes(Asset(main, "Conflict.bin"), new byte[] { 3 });
            File.WriteAllBytes(Asset(workspace, "Applied.bin"), new byte[] { 4 }); File.WriteAllBytes(Asset(main, "Applied.bin"), new byte[] { 4 });
            File.WriteAllBytes(Asset(main, "MainOnly.bin"), new byte[] { 5 });
            string mainBefore = Fingerprint(main);
            var plan = Plan(snapshot.BaselineHash);
            Assert.That(Fingerprint(main), Is.EqualTo(mainBefore));
            Assert.That(Change(plan, "Changed.bin").status, Is.EqualTo("ready"));
            Assert.That(Change(plan, "Added.bin").operation, Is.EqualTo("added"));
            Assert.That(Change(plan, "Added.bin.meta").operation, Is.EqualTo("added"));
            Assert.That(Change(plan, "Deleted.bin").operation, Is.EqualTo("deleted"));
            Assert.That(Change(plan, "Deleted.bin.meta").operation, Is.EqualTo("deleted"));
            Assert.That(Change(plan, "Conflict.bin").status, Is.EqualTo("conflict"));
            Assert.That(Change(plan, "Applied.bin").status, Is.EqualTo("already_applied"));
            Assert.That(plan.entries.Any(item => item.path == "Assets/MainOnly.bin"), Is.False);
            Assert.That(plan.mainOnlyChanges, Is.EqualTo(1));
            var changed = Change(plan, "Changed.bin");
            string payload = Path.Combine(sidecar, "plan", changed.payloadHash + ".bin");
            File.WriteAllBytes(Asset(workspace, "Changed.bin"), new byte[] { 99 });
            Assert.That(File.ReadAllBytes(payload), Is.EqualTo(new byte[] { 255, 0, 128 }));
            Assert.That(Fingerprint(main), Is.EqualTo(mainBefore));
        }

        [Test]
        public void InitialPrivateVendoringDoesNotCreateUnsupportedChangesButLaterEditsDo()
        {
            Put(main, "Marker.txt", "main");
            string local = Path.Combine(root, "local"); Directory.CreateDirectory(local);
            File.WriteAllText(Path.Combine(local, "package.json"), "{\"name\":\"com.example.local\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(local, "Marker.txt"), "package");
            File.WriteAllText(Path.Combine(main, "Packages", "manifest.json"), new JObject { ["dependencies"] = new JObject { ["com.example.local"] = "file:" + local.Replace('\\', '/') } }.ToString());
            var snapshot = Snapshot();
            var initial = Plan(snapshot.BaselineHash, "initial");
            Assert.That(initial.entries, Is.Empty);
            Assert.That(File.Exists(ManagedSessionIntegration.BaselinePath(sidecar, snapshot.BaselineHash)), Is.True);
            Assert.That(Directory.EnumerateFiles(workspace, "b-*.json", SearchOption.AllDirectories), Is.Empty);
            File.WriteAllText(Path.Combine(workspace, "ProjectSettings", "Changed.asset"), "setting");
            File.AppendAllText(Path.Combine(workspace, "Packages", "manifest.json"), "\n");
            File.WriteAllText(Path.Combine(workspace, "ManagedVendor", "com.example.local", "Marker.txt"), "edited package");
            string before = Fingerprint(main);
            var plan = Plan(snapshot.BaselineHash);
            Assert.That(plan.entries.Count(item => item.status == "unsupported"), Is.EqualTo(3));
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }

        [TestCase("credentials.json")]
        [TestCase("Signing.KEY")]
        public void RegistrationExcludesSensitiveAssetAndMetadataTogetherAndCapturesValidBaseline(string sensitiveName)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) Assert.Ignore("Managed registration requires Windows.");
            Put(main, sensitiveName, "excluded fixture data");
            Put(main, "SecretDoor.cs", "// legitimate game source\n");
            var manager = new ManagedSessionManager(main, "unused", "6000.0.75f1", Path.Combine(root, "storage"));
            try
            {
                var registration = manager.Execute("session", new JObject { ["action"] = "register", ["label"] = "sensitive metadata regression", ["requestId"] = "register-sensitive-pair" });
                Assert.That(registration.Property("error"), Is.Null, registration.ToString());
                var session = manager.Sessions.Single();
                for (int attempt = 0; attempt < 500 && session.state == "snapshotting"; attempt++) { manager.Tick(); Thread.Sleep(10); }
                Assert.That(session.state, Is.EqualTo("ready"), session.error);
                Assert.That(File.Exists(Asset(session.workspace, sensitiveName)), Is.False);
                Assert.That(File.Exists(Asset(session.workspace, sensitiveName + ".meta")), Is.False);
                Assert.That(File.Exists(Asset(session.workspace, "SecretDoor.cs")), Is.True);
                Assert.That(File.Exists(Asset(session.workspace, "SecretDoor.cs.meta")), Is.True);
                var report = JObject.Parse(File.ReadAllText(Path.Combine(session.workspace, "managed-snapshot-report.json")));
                Assert.That(report["excludedPaths"].Values<string>(), Does.Contain("Assets/" + sensitiveName));
                Assert.That(report["excludedPaths"].Values<string>(), Does.Contain("Assets/" + sensitiveName + ".meta"));
                Assert.Throws<InvalidDataException>(() => ManagedSessionFiles.AssetPath(session.workspace, "Assets/" + sensitiveName + ".meta"));
                string baselinePath = ManagedSessionIntegration.BaselinePath(manager.IntegrationDirectory(session), session.integrationBaselineHash);
                var baseline = JsonConvert.DeserializeObject<ManagedIntegrationBaseline>(File.ReadAllText(baselinePath));
                Assert.That(baseline.files.Any(item => item.path == "Assets/" + sensitiveName || item.path == "Assets/" + sensitiveName + ".meta"), Is.False);
                Assert.That(baseline.files.Any(item => item.path == "Assets/SecretDoor.cs.meta"), Is.True);
                string planDirectory = Path.Combine(manager.IntegrationDirectory(session), "sensitive-preview");
                string planHash = ManagedSessionIntegration.CreatePlan(main, session.workspace, manager.IntegrationDirectory(session), session.integrationBaselineHash,
                    planDirectory, "sensitive-preview", session.id, CancellationToken.None);
                Assert.That(ManagedSessionIntegration.ReadPlan(planDirectory, planHash).entries, Is.Empty);
            }
            finally { manager.Shutdown(); }
        }

        [Test]
        public void MissingAndTamperedBaselineNeverFallBackToCurrentMain()
        {
            Put(main, "Marker.txt", "main"); var snapshot = Snapshot(); string before = Fingerprint(main);
            var missing = Assert.Throws<ManagedIntegrationException>(() => Plan(null));
            Assert.That(missing.Code, Is.EqualTo("BASELINE_UNAVAILABLE"));
            File.AppendAllText(ManagedSessionIntegration.BaselinePath(sidecar, snapshot.BaselineHash), " ");
            Assert.That(Assert.Throws<ManagedIntegrationException>(() => Plan(snapshot.BaselineHash)).Code, Is.EqualTo("BASELINE_INTEGRITY"));
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }

        [Test]
        public void PlanMetadataTamperingIsRejectedAndLaterSourceEditsCannotAlterFrozenPlan()
        {
            Put(main, "Marker.txt", "main"); var snapshot = Snapshot(); File.WriteAllText(Asset(workspace, "Marker.txt"), "private");
            string directory = Path.Combine(sidecar, "plan");
            string hash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, snapshot.BaselineHash, directory, "plan", "session", CancellationToken.None);
            File.WriteAllText(Asset(main, "Marker.txt"), "later main"); File.WriteAllText(Asset(workspace, "Marker.txt"), "later private");
            Assert.That(Change(ManagedSessionIntegration.ReadPlan(directory, hash), "Marker.txt").status, Is.EqualTo("ready"));
            File.AppendAllText(Path.Combine(directory, "plan.json"), " ");
            Assert.That(Assert.Throws<ManagedIntegrationException>(() => ManagedSessionIntegration.ReadPlan(directory, hash)).Code, Is.EqualTo("PLAN_INTEGRITY"));
        }

        [TestCase("missing_meta", "ASSET_META_INCOMPLETE")]
        [TestCase("secret", "UNSAFE_PATH")]
        [TestCase("duplicate_guid", "GUID_CONFLICT")]
        [TestCase("main_guid_collision", "GUID_CONFLICT")]
        [TestCase("type_change", "ASSET_TYPE_CONFLICT")]
        public void UnsafeAssetChangesFailClosedWithoutMainWrites(string scenario, string code)
        {
            string guid = Guid.NewGuid().ToString("N"); Put(main, "Marker.txt", "main", guid); var snapshot = Snapshot();
            if (scenario == "missing_meta") File.WriteAllText(Asset(workspace, "New.txt"), "missing");
            if (scenario == "secret") File.WriteAllText(Asset(workspace, ".env"), "excluded");
            if (scenario == "duplicate_guid") Put(workspace, "New.txt", "new", guid);
            if (scenario == "main_guid_collision") { string shared = Guid.NewGuid().ToString("N"); Put(main, "MainOnly.txt", "new", shared); Put(workspace, "PrivateOnly.txt", "new", shared); }
            if (scenario == "type_change") { File.Delete(Asset(workspace, "Marker.txt")); Directory.CreateDirectory(Asset(workspace, "Marker.txt")); }
            string before = Fingerprint(main);
            Assert.That(Assert.Throws<ManagedIntegrationException>(() => Plan(snapshot.BaselineHash)).Code, Is.EqualTo(code));
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }

        [Test]
        public void ActualOversizedSourceIsRejectedBeforeHashingRatherThanClipped()
        {
            Put(main, "Marker.txt", "main"); var snapshot = Snapshot();
            using (var stream = new FileStream(Asset(workspace, "Large.bin"), FileMode.CreateNew, FileAccess.Write)) stream.SetLength(ManagedSessionFiles.MaximumSourceFileBytes + 1);
            string before = Fingerprint(main);
            var error = Assert.Throws<InvalidDataException>(() => Plan(snapshot.BaselineHash));
            StringAssert.Contains("SNAPSHOT_LIMIT", error.Message);
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }

        [Test]
        public void CancellationStopsPreviewWithoutMainWritesOrCompletedPlan()
        {
            Put(main, "Marker.txt", "main"); var snapshot = Snapshot(); string before = Fingerprint(main);
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                Assert.Throws<OperationCanceledException>(() => ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, snapshot.BaselineHash, Path.Combine(sidecar, "cancelled"), "plan", "session", cancellation.Token));
            }
            Assert.That(File.Exists(Path.Combine(sidecar, "cancelled", "plan.json")), Is.False);
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }

        [Test]
        public void ManagerChecksLeaseWithoutTickAllowsClosedPreviewAndKeepsReceiptAndPaginationStable()
        {
            Put(main, "Marker.txt", "main");
            var manager = new ManagedSessionManager(main, "unused", "6000.0.75f1", Path.Combine(root, "storage"));
            var session = new ManagedSessionRecord { id = "session-" + Guid.NewGuid().ToString("N"), state = "closed", workspace = workspace, leaseUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 60000 };
            string capability = ManagedSessionFiles.NewCapability(); session.capabilityHash = ManagedSessionFiles.HashText(capability);
            session.integrationBaselineHash = ManagedSessionIntegration.Snapshot(main, workspace, manager.IntegrationDirectory(session), CancellationToken.None).BaselineHash;
            var state = (ManagedSessionState)typeof(ManagedSessionManager).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager); state.sessions.Add(session);
            for (int index = 0; index < 65; index++) Put(workspace, "New" + index + ".txt", "private");
            var request = new JObject { ["action"] = "integration_plan", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "preview" };
            string before = Fingerprint(main);
            var accepted = manager.Execute("workspace", request);
            AssertNativeSuccessContract(accepted);
            string planId = accepted.Value<string>("planId");
            request["action"] = "integration_status"; request["planId"] = planId;
            JObject result = null;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                result = manager.Execute("workspace", request);
                if (result.Value<string>("state") != "planning") break;
                Thread.Sleep(10);
            }
            Assert.That(result.Value<string>("state"), Is.EqualTo("ready"), result.ToString());
            AssertNativeSuccessContract(result);
            Assert.That(result.Value<bool>("canApply"), Is.True, "Preview eligibility still requires a separate explicit apply request.");
            Assert.That(result.Value<int>("totalEntries"), Is.EqualTo(130));
            Assert.That(result["entries"].Count(), Is.EqualTo(100)); Assert.That(result.Value<int>("nextOffset"), Is.EqualTo(100));
            request["offset"] = 100; result = manager.Execute("workspace", request);
            AssertNativeSuccessContract(result);
            Assert.That(result["entries"].Count(), Is.EqualTo(30)); Assert.That(result["nextOffset"].Type, Is.EqualTo(JTokenType.Null));
            request["offset"] = int.MaxValue; request["limit"] = 500;
            result = manager.Execute("workspace", request); AssertNativeSuccessContract(result);
            Assert.That(result["entries"], Is.Empty); Assert.That(result["nextOffset"].Type, Is.EqualTo(JTokenType.Null));
            Assert.That(result.Value<int>("totalEntries"), Is.EqualTo(130));
            request["offset"] = 129; request["limit"] = 1;
            result = manager.Execute("workspace", request); AssertNativeSuccessContract(result);
            Assert.That(result["entries"].Count(), Is.EqualTo(1)); Assert.That(result["nextOffset"].Type, Is.EqualTo(JTokenType.Null));
            request["action"] = "integration_plan";
            Assert.That(manager.Execute("workspace", request).Value<string>("planId"), Is.EqualTo(planId));
            Assert.That(state.integrations.Count, Is.EqualTo(1)); Assert.That(session.state, Is.EqualTo("closed"));
            request["capability"] = "foreign"; Assert.That(manager.Execute("workspace", request).Value<string>("code"), Is.EqualTo("OWNER_DENIED"));
            request["capability"] = capability; session.leaseUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
            Assert.That(manager.Execute("workspace", request).Value<string>("code"), Is.EqualTo("OWNER_DENIED"));
            request["action"] = "integration_status"; Assert.That(manager.Execute("workspace", request).Value<string>("code"), Is.EqualTo("OWNER_DENIED"));
            Assert.That(Fingerprint(main), Is.EqualTo(before));
        }
    }
}
