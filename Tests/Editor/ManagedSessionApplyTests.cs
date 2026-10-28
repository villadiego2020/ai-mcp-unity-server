using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    public sealed class ManagedSessionApplyTests
    {
        string root, main, workspace, sidecar, planDirectory, operationDirectory, baselineHash, planHash, preparationHash;
        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "AIUnityMCPServer-apply-" + Guid.NewGuid().ToString("N"));
            main = Path.Combine(root, "main"); workspace = Path.Combine(root, "private"); sidecar = Path.Combine(root, "sidecar");
            planDirectory = Path.Combine(sidecar, "plan"); operationDirectory = Path.Combine(root, "operation");
            Directory.CreateDirectory(Path.Combine(main, "Assets")); Directory.CreateDirectory(Path.Combine(main, "Packages")); Directory.CreateDirectory(Path.Combine(main, "ProjectSettings"));
            File.WriteAllText(Path.Combine(main, "Packages", "manifest.json"), "{\"dependencies\":{}}");
            Put(main, "Marker.txt", "baseline");
        }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }
        string Target(string project) => Path.Combine(project, "Assets", "Marker.txt");
        static void Put(string project, string name, string text)
        {
            string path = Path.Combine(project, "Assets", name); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path, text);
            File.WriteAllText(path + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
        }
        void Prepare()
        {
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            File.WriteAllText(Target(workspace), "private-review");
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "plan-test", "session-test", CancellationToken.None);
            preparationHash = ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None);
        }

        [Test]
        public void SuccessfulPublicationPreservesUnrelatedMainAndAdvancesOnlyPublishedBaselinePaths()
        {
            Put(main, "Unrelated.txt", "original unrelated");
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            File.WriteAllText(Target(workspace), "published");
            File.WriteAllText(Path.Combine(main, "Assets", "Unrelated.txt"), "independent Main edit"); Put(main, "MainOnly.txt", "independent addition");
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "plan-test", "session-test", CancellationToken.None);
            preparationHash = ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None);
            string advanced = ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash);
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("published"));
            Assert.That(File.ReadAllText(Path.Combine(main, "Assets", "Unrelated.txt")), Is.EqualTo("independent Main edit"));
            Assert.That(File.Exists(Path.Combine(main, "Assets", "MainOnly.txt")), Is.True);
            Assert.That(ManagedSessionIntegration.IsPristine(workspace, sidecar, advanced, CancellationToken.None), Is.True, "Unrelated Main edits must not become fabricated private deltas.");
            File.WriteAllText(Target(workspace), "corrective edit");
            string nextDirectory = Path.Combine(sidecar, "corrective");
            string nextHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, advanced, nextDirectory, "plan-corrective", "session-test", CancellationToken.None);
            Assert.That(ManagedSessionIntegration.ReadPlan(nextDirectory, nextHash).entries.Single(item => item.path == "Assets/Marker.txt").status, Is.EqualTo("ready"));
            Assert.That(File.Exists(ManagedSessionIntegration.BaselinePath(sidecar, baselineHash)), Is.True, "Earlier baseline generations are immutable retained evidence.");
        }

        [TestCase("after_write")]
        [TestCase("before_sources_applied")]
        public void ControlledFailureRollsBackOnlyVerifiedTransactionPostimages(string phase)
        {
            Prepare();
            var error = Assert.Throws<ManagedApplyFailure>(() => ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash,
                (index, current) => { if (current == phase) throw new IOException("Controlled fault"); }));
            Assert.That(error.RecoveryRequired, Is.False);
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
            Assert.That(ManagedSessionIntegration.InspectJournal(operationDirectory).state, Is.EqualTo("rolled_back"));
        }

        [Test]
        public void RollbackPreservesAnObservedExternalThirdHash()
        {
            Prepare();
            var error = Assert.Throws<ManagedApplyFailure>(() => ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash,
                (index, phase) => { if (phase == "after_write") { File.WriteAllText(Target(main), "external-third"); throw new IOException("Controlled external change"); } }));
            Assert.That(error.RecoveryRequired, Is.True);
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("external-third"));
            Assert.That(ManagedSessionIntegration.InspectJournal(operationDirectory).state, Is.EqualTo("recovery_required"));
        }

        [Test]
        public void RollbackPreservesThirdHashArrivingDuringRestoreStagingWithoutActorThrowing()
        {
            Prepare();
            var error = Assert.Throws<ManagedApplyFailure>(() => ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash,
                (index, phase) =>
                {
                    if (phase == "after_write") throw new IOException("Enter rollback");
                    if (phase == "after_restore_staging") File.WriteAllText(Target(main), "external-during-restore");
                }));
            Assert.That(error.RecoveryRequired, Is.True);
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("external-during-restore"));
        }

        [Test]
        public void RollbackJournalPersistenceFailureReportsRecoveryRequiredWithPreservedSource()
        {
            Prepare(); FileStream locked = null; Exception observed = null;
            try
            {
                ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash, (index, phase) =>
                {
                    if (phase != "after_write") return;
                    locked = new FileStream(Path.Combine(operationDirectory, "journal.json"), FileMode.Open, FileAccess.Read, FileShare.None);
                    throw new IOException("Controlled failure with locked rollback journal");
                });
            }
            catch (Exception error) { observed = error; }
            finally { locked?.Dispose(); }
            TestContext.WriteLine("Rollback journal lock outcome=" + observed?.GetType().Name + "; Main=" + File.ReadAllText(Target(main)) + "; durable journal=" + ManagedSessionIntegration.InspectJournal(operationDirectory).state);
            Assert.That(observed, Is.TypeOf<ManagedApplyFailure>());
            Assert.That(((ManagedApplyFailure)observed).RecoveryRequired, Is.True);
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
        }

        [Test]
        public void IdenticalMainRefreshReusesVerifiedImmutableBaselineWithoutOverwritingIt()
        {
            var first = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None);
            string baseline = ManagedSessionIntegration.BaselinePath(sidecar, first.BaselineHash);
            byte[] original = File.ReadAllBytes(baseline); DateTime written = File.GetLastWriteTimeUtc(baseline);
            // Control the original unconditional CreateNew baseline write on the captured
            // document: it fails for this exact existing content-addressed generation.
            var oldWrite = typeof(ManagedSessionIntegration).GetMethod("WriteNew", BindingFlags.Static | BindingFlags.NonPublic);
            var control = Assert.Throws<TargetInvocationException>(() => oldWrite.Invoke(null, new object[] { baseline, System.Text.Encoding.UTF8.GetString(original) }));
            Assert.That(control.InnerException, Is.TypeOf<IOException>());
            var second = ManagedSessionIntegration.Snapshot(main, Path.Combine(root, "fresh-generation"), sidecar, CancellationToken.None);
            Assert.That(second.BaselineHash, Is.EqualTo(first.BaselineHash));
            Assert.That(File.ReadAllBytes(baseline), Is.EqualTo(original)); Assert.That(File.GetLastWriteTimeUtc(baseline), Is.EqualTo(written));
            Assert.That(File.Exists(Target(workspace)), Is.True, "The earlier workspace remains retained.");
        }

        [TestCase("main")]
        [TestCase("private")]
        [TestCase("payload")]
        public void FinalPreflightRejectsChangedReviewedSourcesOrStagedBytesWithoutMainWrites(string changed)
        {
            Prepare();
            if (changed == "main") File.WriteAllText(Target(main), "new Main");
            if (changed == "private") File.WriteAllText(Target(workspace), "new private");
            if (changed == "payload")
            {
                var prepared = ManagedSessionIntegration.ReadPreparation(operationDirectory, preparationHash);
                File.WriteAllText(Path.Combine(operationDirectory, prepared.entries.First().staged), "tampered staging");
            }
            string before = File.ReadAllText(Target(main));
            Assert.Throws<ManagedIntegrationException>(() => ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash));
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo(before));
        }

        [TestCase("MainOnly.txt")]
        [TestCase("credentials.json")]
        public void FolderDeletionPreviewAndLateApplyPreflightPreserveUnreviewedRawDescendants(string child)
        {
            Directory.CreateDirectory(Path.Combine(main, "Assets", "Folder"));
            File.WriteAllText(Path.Combine(main, "Assets", "Folder.meta"), "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nfolderAsset: yes\n");
            Put(main, "Folder/Original.txt", "original");
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            string privateFolder = Path.GetFullPath(Path.Combine(workspace, "Assets", "Folder"));
            Assert.That(privateFolder.StartsWith(Path.GetFullPath(workspace) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
            Directory.Delete(privateFolder, true); File.Delete(Path.Combine(workspace, "Assets", "Folder.meta"));
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "reviewed-delete", "session-test", CancellationToken.None);
            Put(main, "Folder/" + child, "Main-only data");
            Assert.That(Assert.Throws<ManagedIntegrationException>(() => ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None)).Code,
                Is.EqualTo("UNREVIEWED_DESCENDANTS"));
            string newDirectory = Path.Combine(sidecar, "new-review");
            string newHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, newDirectory, "new-delete-review", "session-test", CancellationToken.None);
            Assert.That(ManagedSessionIntegration.ReadPlan(newDirectory, newHash).entries.Single(item => item.path == "Assets/Folder").status, Is.EqualTo("conflict"));
            Assert.That(File.ReadAllText(Path.Combine(main, "Assets", "Folder", child)), Is.EqualTo("Main-only data"));
            Assert.That(File.Exists(Path.Combine(main, "Assets", "Folder", "Original.txt")), Is.True);
        }

        [TestCase("removed")]
        [TestCase("replaced_with_file")]
        public void MissingOrReplacedReviewedParentRejectsPublicationBeforeAnySourceWrites(string mode)
        {
            string folder = Path.Combine(main, "Assets", "Folder"); Directory.CreateDirectory(folder);
            File.WriteAllText(folder + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\nfolderAsset: yes\n");
            Put(main, "Folder/Original.txt", "original");
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            Put(workspace, "Folder/New.txt", "new private child"); File.WriteAllText(Target(workspace), "other reviewed edit");
            Assert.That(Path.GetFullPath(folder).StartsWith(Path.GetFullPath(main) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), Is.True);
            Directory.Delete(folder, true); File.Delete(folder + ".meta");
            if (mode == "replaced_with_file") Put(main, "Folder", "replacement file");
            Exception observed = null;
            try
            {
                planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "parent-plan", "session-test", CancellationToken.None);
                ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None);
            }
            catch (Exception error) { observed = error; }
            TestContext.WriteLine("Parent=" + mode + "; preparation=" + (observed?.GetType().Name ?? "accepted") + "; Main Marker=" + File.ReadAllText(Target(main)));
            Assert.That(observed, Is.TypeOf<ManagedIntegrationException>(), "Missing/replaced parents must be rejected during preview or preflight, before a later File.Move can fail after earlier writes.");
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
        }

        [Test]
        [Timeout(300000)]
        public void ActualSeventySixMiBAssetCanBePublishedWithinDeclaredPayloadBudget()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            string binary = Path.Combine(main, "Assets", "Large.bin");
            using (var stream = new FileStream(binary, FileMode.CreateNew, FileAccess.Write)) stream.SetLength(76L * 1024 * 1024);
            File.WriteAllText(binary + ".meta", "fileFormatVersion: 2\nguid: " + Guid.NewGuid().ToString("N") + "\n");
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            TestContext.WriteLine("76MiB snapshot+baseline seconds=" + timer.Elapsed.TotalSeconds); timer.Restart();
            using (var stream = new FileStream(Path.Combine(workspace, "Assets", "Large.bin"), FileMode.Open, FileAccess.Write)) stream.WriteByte(42);
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "large-plan", "session-test", CancellationToken.None);
            TestContext.WriteLine("76MiB preview seconds=" + timer.Elapsed.TotalSeconds); timer.Restart();
            preparationHash = ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None);
            TestContext.WriteLine("76MiB asynchronous preparation seconds=" + timer.Elapsed.TotalSeconds); timer.Restart();
            ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash);
            TestContext.WriteLine("76MiB synchronous filesystem commit seconds=" + timer.Elapsed.TotalSeconds);
            using (var stream = File.OpenRead(binary)) { Assert.That(stream.Length, Is.EqualTo(76L * 1024 * 1024)); Assert.That(stream.ReadByte(), Is.EqualTo(42)); }
        }

        ManagedSessionManager OwnedManager(out ManagedSessionRecord session, out string capability, out ManagedIntegrationRecord preview, Action<string, string> markerWriter = null)
        {
            var manager = new ManagedSessionManager(main, "unused", "6000.0.75f1", Path.Combine(root, "storage"), markerWriter);
            capability = ManagedSessionFiles.NewCapability();
            session = new ManagedSessionRecord { id = "session-" + Guid.NewGuid().ToString("N"), state = "ready", workspace = workspace, capabilityHash = ManagedSessionFiles.HashText(capability), leaseUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 120000, workOrder = 1 };
            session.integrationBaselineHash = ManagedSessionIntegration.Snapshot(main, workspace, manager.IntegrationDirectory(session), CancellationToken.None).BaselineHash;
            File.WriteAllText(Target(workspace), "private-review");
            string id = "plan-" + Guid.NewGuid().ToString("N"), directory = Path.Combine(manager.IntegrationDirectory(session), "p-" + id.Substring(5, 16));
            string hash = ManagedSessionIntegration.CreatePlan(main, workspace, manager.IntegrationDirectory(session), session.integrationBaselineHash, directory, id, session.id, CancellationToken.None);
            preview = new ManagedIntegrationRecord { id = id, sessionId = session.id, baselineHash = session.integrationBaselineHash, planHash = hash, state = "ready" };
            var registry = Registry(manager); registry.sessions.Add(session); registry.integrations.Add(preview); registry.nextWorkOrder = 2;
            return manager;
        }
        static ManagedSessionState Registry(ManagedSessionManager manager) => (ManagedSessionState)typeof(ManagedSessionManager).GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);

        [Test]
        public void AcceptedApplyIsPersistedIdempotentAndOrdersEarlierBuildBeforeLaterSnapshotWithoutMainWrites()
        {
            var manager = OwnedManager(out var session, out string capability, out var preview); bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = true;
                var registry = Registry(manager);
                registry.builds.Add(new ManagedBuildRecord { id = "build-" + Guid.NewGuid().ToString("N"), sessionId = session.id, state = "queued", project = Path.Combine(root, "earlier-build"), workOrder = registry.nextWorkOrder++, created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
                var request = new JObject { ["action"] = "integration_apply", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "apply-once", ["planId"] = preview.id, ["planHash"] = preview.planHash };
                var accepted = manager.Execute("workspace", request);
                Assert.That(accepted.Property("error"), Is.Null, accepted.ToString()); Assert.That(accepted.Value<string>("state"), Is.EqualTo("queued"));
                string id = accepted.Value<string>("operationId");
                Assert.That(manager.Execute("workspace", request).Value<string>("operationId"), Is.EqualTo(id)); Assert.That(registry.mainOperations.Count, Is.EqualTo(1));
                var persisted = JObject.Parse(File.ReadAllText(Path.Combine(manager.Root, "registry.json")));
                Assert.That(persisted["mainOperations"].Count(), Is.EqualTo(1)); Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
                var later = manager.Execute("session", new JObject { ["action"] = "register", ["label"] = "later snapshot", ["requestId"] = "later" });
                Assert.That(later.Value<string>("state"), Is.EqualTo("queued")); Assert.That(Directory.Exists(later.Value<string>("workspacePath")), Is.False);
                Assert.That(manager.CanStartWork(registry.builds.Single().workOrder), Is.True, "Earlier builds must drain rather than deadlock behind the Main operation waiting for them.");
                Assert.That(manager.CanStartWork(registry.sessions.Last().workOrder), Is.False, "Later Main snapshots must observe the published revision, not race the commit.");
                request["action"] = "integration_status"; request["operationId"] = id;
                Assert.That(manager.Execute("workspace", request).Property("error"), Is.Null);
                MCPHandlers.AllowWrites = false; request["action"] = "integration_apply"; request["requestId"] = "write-off";
                Assert.That(manager.Execute("workspace", request).Value<string>("code"), Is.EqualTo("WRITE_DISABLED"));
                session.state = "closed"; request["action"] = "integration_cancel"; request["requestId"] = "cancel-closed-owned";
                var cancelled = manager.Execute("workspace", request);
                Assert.That(cancelled.Value<bool>("cancelAccepted"), Is.True, cancelled.ToString()); Assert.That(cancelled.Value<string>("state"), Is.EqualTo("cancelled"));
                Assert.That(manager.Execute("workspace", request).Value<string>("operationId"), Is.EqualTo(id));
                request["action"] = "integration_apply"; session.leaseUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1;
                Assert.That(manager.Execute("workspace", request).Value<string>("code"), Is.EqualTo("OWNER_DENIED"));
                Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
            }
            finally { manager.Shutdown(); MCPHandlers.AllowWrites = writes; }
        }

        [TestCase("missing")]
        [TestCase("malformed")]
        public void FailedOrMalformedAcceptanceFenceCannotPromoteAnOperationAfterRestart(string mode)
        {
            Action<string, string> writer = (path, content) => { if (mode == "missing") throw new IOException("Controlled marker write failure"); File.WriteAllText(path, "malformed"); };
            var manager = OwnedManager(out var session, out string capability, out var preview, writer); bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = true;
                var result = manager.Execute("workspace", new JObject { ["action"] = "integration_apply", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "failed-fence", ["planId"] = preview.id, ["planHash"] = preview.planHash });
                Assert.That(result.Property("error"), Is.Not.Null); Assert.That(manager.MainOperations, Is.Empty);
                var recovered = new ManagedSessionManager(main, "unused", "6000.0.75f1", manager.Root);
                Assert.That(recovered.MainOperations.Single().state, Is.EqualTo("failed")); Assert.That(recovered.MainOperations.Single().failureCode, Is.EqualTo("ADMISSION_INTERRUPTED"));
                Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline")); recovered.Shutdown();
            }
            finally { MCPHandlers.AllowWrites = writes; }
        }

        [Test]
        public void ValidAcceptanceFenceAfterWriterExceptionReturnsStableReceiptAndRecoversExactlyOnce()
        {
            var manager = OwnedManager(out var session, out string capability, out var preview, (path, content) => { ManagedSessionFiles.WriteAtomic(path, content); throw new IOException("Controlled exception after valid marker"); });
            bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = true;
                var request = new JObject { ["action"] = "integration_apply", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "durably-accepted", ["planId"] = preview.id, ["planHash"] = preview.planHash };
                var accepted = manager.Execute("workspace", request);
                Assert.That(accepted.Property("error"), Is.Null, accepted.ToString()); Assert.That(accepted.Value<string>("state"), Is.EqualTo("queued"));
                var disk = JObject.Parse(File.ReadAllText(Path.Combine(manager.Root, "registry.json")));
                Assert.That(disk["mainOperations"][0].Value<string>("state"), Is.EqualTo("admission_pending"));
                var recovered = new ManagedSessionManager(main, "unused", "6000.0.75f1", manager.Root);
                Assert.That(recovered.MainOperations.Single().state, Is.EqualTo("queued"));
                Assert.That(recovered.Execute("workspace", request).Value<string>("operationId"), Is.EqualTo(accepted.Value<string>("operationId")));
                Assert.That(recovered.MainOperations.Count, Is.EqualTo(1)); Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline")); recovered.Shutdown();
            }
            finally { MCPHandlers.AllowWrites = writes; }
        }

        [TestCase("lease")]
        [TestCase("write_gate")]
        public void AuthorityLostAfterIntentBeforeFirstWriteLeavesMainUnchanged(string lost)
        {
            var manager = OwnedManager(out var session, out string capability, out var preview); bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = true;
                var accepted = manager.Execute("workspace", new JObject { ["action"] = "integration_apply", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "last-moment-authority", ["planId"] = preview.id, ["planHash"] = preview.planHash });
                Assert.That(accepted.Property("error"), Is.Null, accepted.ToString());
                var operation = manager.MainOperations.Single(); operation.activeDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ManagedSessionManager.MainOperationGraceMilliseconds;
                string plan = Path.Combine(manager.IntegrationDirectory(session), "p-" + preview.id.Substring(5, 16));
                string prepared = ManagedSessionIntegration.PrepareApply(main, workspace, manager.IntegrationDirectory(session), session.integrationBaselineHash, plan, preview.planHash, operationDirectory, CancellationToken.None);
                Assert.Throws<ManagedApplyFailure>(() => ManagedSessionIntegration.CommitApply(main, workspace, manager.IntegrationDirectory(session), operationDirectory, prepared,
                    (index, phase) => { if (phase == "after_intent") { if (lost == "lease") session.leaseUntil = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1; else MCPHandlers.AllowWrites = false; } },
                    () => manager.ValidateMainWriteAuthority(session, operation)));
                Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
            }
            finally { manager.Shutdown(); MCPHandlers.AllowWrites = writes; }
        }

        [Test]
        public void OnceFirstWriteStartsLaterAuthorityLossDoesNotPretendThatNothingWasPublished()
        {
            Put(main, "Other.txt", "other baseline");
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            File.WriteAllText(Target(workspace), "first published"); File.WriteAllText(Path.Combine(workspace, "Assets", "Other.txt"), "second published");
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "two-file-plan", "session-test", CancellationToken.None);
            preparationHash = ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None);
            bool authority = true; int checks = 0;
            ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash,
                (index, phase) => { if (phase == "after_write") authority = false; },
                () => { checks++; if (!authority) throw new UnauthorizedAccessException("Expired after the first write"); });
            Assert.That(checks, Is.EqualTo(1)); Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("first published"));
            Assert.That(File.ReadAllText(Path.Combine(main, "Assets", "Other.txt")), Is.EqualTo("second published"));
            Assert.That(ManagedSessionIntegration.InspectJournal(operationDirectory).state, Is.EqualTo("sources_applied"));
        }

        [Test]
        public void RejectedAdmissionCannotLeaveARunnableMainOperationAfterRegistryUnlock()
        {
            var manager = OwnedManager(out var session, out string capability, out var preview); bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = true;
                JObject result;
                using (var locked = new FileStream(Path.Combine(manager.Root, "registry.json"), FileMode.Open, FileAccess.Read, FileShare.None))
                    result = manager.Execute("workspace", new JObject { ["action"] = "integration_apply", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "rejected-admission", ["planId"] = preview.id, ["planHash"] = preview.planHash });
                typeof(ManagedSessionManager).GetMethod("TickMainOperations", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
                TestContext.WriteLine("Rejected admission reply=" + result.ToString(Newtonsoft.Json.Formatting.None) + "; retained operations=" + manager.MainOperations.Count + "; next state=" + manager.MainOperations.FirstOrDefault()?.state);
                Assert.That(result.Property("error"), Is.Not.Null);
                Assert.That(manager.MainOperations, Is.Empty, "A rejected acceptance must not become a preparing/runnable job after the state file unlocks.");
                Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
            }
            finally
            {
                manager.Shutdown(); MCPHandlers.AllowWrites = writes;
                var tasks = (System.Collections.IDictionary)typeof(ManagedSessionManager).GetField("applyPreparations", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(manager);
                for (int attempt = 0; attempt < 200 && tasks.Values.Cast<System.Threading.Tasks.Task>().Any(task => !task.IsCompleted); attempt++) Thread.Sleep(10);
            }
        }

        [Test]
        public void HeartbeatFutureGraceAppliesOnlyToActiveMainWorkAndSurvivesReloadCheckpoint()
        {
            var manager = OwnedManager(out var session, out _, out _);
            var operation = new ManagedMainOperationRecord { id = "op-" + Guid.NewGuid().ToString("N"), sessionId = session.id, state = "queued", activeDeadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ManagedSessionManager.MainOperationGraceMilliseconds };
            Registry(manager).mainOperations.Add(operation);
            Assert.That(manager.HeartbeatStamp(false), Is.LessThan(operation.activeDeadline - 100000));
            operation.state = "compiling"; operation.sourceApplied = true;
            Assert.That(manager.HeartbeatStamp(false), Is.EqualTo(operation.activeDeadline)); Assert.That(manager.HeartbeatStamp(true), Is.EqualTo(operation.activeDeadline));
            operation.state = "succeeded";
            Assert.That(manager.HeartbeatStamp(false), Is.LessThan(operation.activeDeadline)); Assert.That(manager.HeartbeatStamp(true), Is.EqualTo(operation.activeDeadline));
            manager.Shutdown();
        }

        [Test]
        public void StartupRecoversDurablyAppliedSourceTruthWithoutReplayingOrOverwritingLaterMainEdits()
        {
            var manager = OwnedManager(out var session, out _, out var preview);
            string id = "op-" + Guid.NewGuid().ToString("N"), operation = Path.Combine(manager.Root, "o-" + id.Substring(3, 16));
            string plan = Path.Combine(manager.IntegrationDirectory(session), "p-" + preview.id.Substring(5, 16));
            string prepared = ManagedSessionIntegration.PrepareApply(main, workspace, manager.IntegrationDirectory(session), session.integrationBaselineHash, plan, preview.planHash, operation, CancellationToken.None);
            string advanced = ManagedSessionIntegration.CommitApply(main, workspace, manager.IntegrationDirectory(session), operation, prepared);
            Registry(manager).mainOperations.Add(new ManagedMainOperationRecord { id = id, sessionId = session.id, kind = "apply", state = "applying", baselineBefore = session.integrationBaselineHash, workOrder = 2 });
            typeof(ManagedSessionManager).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(manager, null);
            File.WriteAllText(Target(main), "external-after-publication");
            var recovered = new ManagedSessionManager(main, "unused", "6000.0.75f1", manager.Root);
            Assert.That(recovered.MainOperations.Single().sourceApplied, Is.True); Assert.That(recovered.MainOperations.Single().state, Is.EqualTo("validation_interrupted"));
            Assert.That(recovered.Sessions.Single().integrationBaselineHash, Is.EqualTo(advanced));
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("external-after-publication"));
            recovered.Shutdown();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RefreshRejectsDirtyPrivateOrCompletesVerifiedNoOpWithoutDiscardingOldSource(bool pristine)
        {
            var manager = OwnedManager(out var session, out string capability, out _); bool writes = MCPHandlers.AllowWrites;
            string originalWorkspace = session.workspace;
            try
            {
                MCPHandlers.AllowWrites = true;
                if (pristine) File.WriteAllText(Target(workspace), "baseline");
                var accepted = manager.Execute("workspace", new JObject { ["action"] = "refresh_from_main", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "refresh-owned" });
                Assert.That(accepted.Property("error"), Is.Null, accepted.ToString());
                var busy = manager.Execute("workspace", new JObject { ["action"] = "write", ["sessionId"] = session.id, ["capability"] = capability, ["requestId"] = "racing-write", ["path"] = "Assets/Marker.txt", ["expectedHash"] = ManagedSessionFiles.Hash(File.ReadAllBytes(Target(workspace))), ["content"] = "racing edit" });
                Assert.That(busy.Value<string>("code"), Is.EqualTo("WORKSPACE_BUSY"));
                var tick = typeof(ManagedSessionManager).GetMethod("TickMainOperations", BindingFlags.Instance | BindingFlags.NonPublic);
                for (int attempt = 0; attempt < 500 && !new[] { "failed", "succeeded" }.Contains(manager.MainOperations.Single().state); attempt++) { tick.Invoke(manager, null); Thread.Sleep(10); }
                var operation = manager.MainOperations.Single();
                Assert.That(operation.state, Is.EqualTo(pristine ? "succeeded" : "failed"), operation.failureDetail);
                Assert.That(pristine ? operation.compileResult : operation.failureCode, Is.EqualTo(pristine ? "workspace_unchanged" : "PRIVATE_CHANGES"));
                Assert.That(session.workspace, Is.EqualTo(originalWorkspace)); Assert.That(session.retainedWorkspaces, Is.Empty);
                Assert.That(File.ReadAllText(Target(workspace)), Is.EqualTo(pristine ? "baseline" : "private-review"));
                Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("baseline"));
            }
            finally { manager.Shutdown(); MCPHandlers.AllowWrites = writes; }
        }

        [Test]
        public void CompileFailedStatusPreservesStructuredDiagnosticsAndRejectsPretendCancellation()
        {
            var manager = OwnedManager(out var session, out string capability, out _); bool writes = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = false;
                var operation = new ManagedMainOperationRecord { id = "op-" + Guid.NewGuid().ToString("N"), sessionId = session.id, kind = "apply", state = "compile_failed", sourceApplied = true,
                    baselineBefore = session.integrationBaselineHash, baselineAfter = session.integrationBaselineHash, compileStarted = true, compileFinished = true, compileResult = "compile_failed", errorCount = 1 };
                operation.diagnostics.Add(new ManagedCompilationDiagnostic { message = "Fixture compile diagnostic", file = "Assets/Marker.cs", line = 1 }); Registry(manager).mainOperations.Add(operation);
                var request = new JObject { ["action"] = "integration_status", ["sessionId"] = session.id, ["capability"] = capability, ["operationId"] = operation.id };
                var status = manager.Execute("workspace", request);
                Assert.That(status.Property("error"), Is.Null); Assert.That(status.Value<bool>("sourceApplied"), Is.True); Assert.That(status["diagnostics"].Count(), Is.EqualTo(1));
                request["action"] = "integration_cancel"; request["requestId"] = "cannot-undo-applied";
                var cancelled = manager.Execute("workspace", request);
                Assert.That(cancelled.Property("error"), Is.Null); Assert.That(cancelled.Value<bool>("cancelAccepted"), Is.False);
                Assert.That(cancelled.Value<string>("state"), Is.EqualTo("compile_failed")); Assert.That(cancelled.Value<bool>("sourceApplied"), Is.True);
            }
            finally { manager.Shutdown(); MCPHandlers.AllowWrites = writes; }
        }

        [Test]
        public void ApplyEntryBoundRejectsActualLargeChangeSetWithoutClippingOrMainWrites()
        {
            baselineHash = ManagedSessionIntegration.Snapshot(main, workspace, sidecar, CancellationToken.None).BaselineHash;
            for (int index = 0; index < 257; index++) Put(workspace, "New" + index + ".txt", "private");
            planHash = ManagedSessionIntegration.CreatePlan(main, workspace, sidecar, baselineHash, planDirectory, "large-entry-plan", "session-test", CancellationToken.None);
            Assert.That(ManagedSessionIntegration.ReadPlan(planDirectory, planHash).entries.Count, Is.EqualTo(514));
            Assert.That(Assert.Throws<ManagedIntegrationException>(() => ManagedSessionIntegration.PrepareApply(main, workspace, sidecar, baselineHash, planDirectory, planHash, operationDirectory, CancellationToken.None)).Code, Is.EqualTo("APPLY_LIMIT"));
            Assert.That(Directory.GetFiles(Path.Combine(main, "Assets")).Length, Is.EqualTo(2)); Assert.That(Directory.Exists(operationDirectory), Is.False);
        }

        [Test]
        public void ApplyPreservesThirdHashArrivingAfterDurableIntentWithoutActorThrowing()
        {
            Prepare(); Exception observed = null;
            try
            {
                ManagedSessionIntegration.CommitApply(main, workspace, sidecar, operationDirectory, preparationHash, (index, phase) =>
                { if (phase == "after_intent") File.WriteAllText(Target(main), "external-third"); });
            }
            catch (Exception error) { observed = error; }
            TestContext.WriteLine("After-intent actor returned normally; final Main bytes=" + File.ReadAllText(Target(main)) + "; commit outcome=" + (observed?.GetType().Name ?? "returned success"));
            Assert.That(File.ReadAllText(Target(main)), Is.EqualTo("external-third"));
            Assert.That(observed, Is.TypeOf<ManagedApplyFailure>());
            Assert.That(((ManagedApplyFailure)observed).RecoveryRequired, Is.True);
        }
    }
}
