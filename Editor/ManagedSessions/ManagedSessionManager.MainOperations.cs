using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace AIUnityMCPServer
{
    [Serializable] internal sealed class ManagedCompilationDiagnostic
    { public string assembly, message, file; public int line, column; }
    [Serializable] internal sealed class ManagedMainOperationRecord
    {
        public string id, sessionId, requestId, kind, state, planId, planHash, baselineBefore, baselineAfter, preparationHash, waitReason, failureCode, failureDetail, admissionHash;
        public string compileAttemptId, compileResult, oldWorkspace, newWorkspace, newRevision, stagedWorkspace, verificationStamp;
        public long workOrder, created, deadline, activeDeadline;
        public bool localAuthority, sourceApplied, refreshIssued, compileRequested, compileStarted, compileFinished;
        public int errorCount;
        public List<ManagedCompilationDiagnostic> diagnostics = new List<ManagedCompilationDiagnostic>();
    }

    internal sealed partial class ManagedSessionManager
    {
        internal const long MainOperationGraceMilliseconds = 600000;
        const int MaximumPendingMainOperations = 4, MaximumRetainedMainOperations = 64, MaximumWorkspaceGenerations = 4;
        readonly Dictionary<string, Task<string>> applyPreparations = new Dictionary<string, Task<string>>();
        readonly Dictionary<string, Task<ManagedSnapshotResult>> refreshPreparations = new Dictionary<string, Task<ManagedSnapshotResult>>();
        readonly Dictionary<string, Task<string>> refreshVerifications = new Dictionary<string, Task<string>>();
        readonly Action<string, string> admissionMarkerWriter;
        object compileContext;
        string contextOperationId;
        bool compilationAttached;
        internal IReadOnlyList<ManagedMainOperationRecord> MainOperations => state.mainOperations;
        internal IReadOnlyList<ManagedIntegrationRecord> IntegrationPlans => state.integrations;
        static bool MainTerminal(ManagedMainOperationRecord operation) => new[] { "succeeded", "compile_failed", "failed", "cancelled", "rolled_back", "recovery_required", "validation_interrupted" }.Contains(operation.state);
        ManagedMainOperationRecord FirstMainOperation => state.mainOperations.Where(item => !MainTerminal(item)).OrderBy(item => item.workOrder).FirstOrDefault();
        string MainOperationDirectory(ManagedMainOperationRecord operation) => Path.Combine(registryRoot, "o-" + operation.id.Substring(3, 16));

        internal bool CanStartWork(long order)
        {
            if (state.mainOperations.Any(item => item.state == "recovery_required")) return false;
            var first = FirstMainOperation;
            return first == null || order < first.workOrder;
        }
        void StartOrderedCopies()
        {
            foreach (var session in state.sessions.Where(item => item.state == "queued" && CanStartWork(item.workOrder)).ToArray())
            {
                session.state = "snapshotting";
                StartCopy(session.id, token => ManagedSessionIntegration.Snapshot(mainProject, session.workspace, IntegrationDirectory(session), token));
            }
            foreach (var build in state.builds.Where(item => item.state == "snapshot_queued" && CanStartWork(item.workOrder)).ToArray())
            {
                build.state = "snapshotting";
                var session = state.sessions.First(item => item.id == build.sessionId);
                StartCopy(build.id, token => new ManagedSnapshotResult(ManagedSessionFiles.Snapshot(session.workspace, build.project, false, token)));
            }
        }
        bool EarlierWorkPending(long order) => state.sessions.Any(item => item.workOrder < order && (item.state == "queued" || item.state == "snapshotting")) ||
            state.builds.Any(item => item.workOrder < order && Active(item));

        JObject AcceptMainOperation(ManagedSessionRecord session, string action, JObject request, bool localAuthority)
        {
            if (!MCPHandlers.AllowWrites) throw new ManagedIntegrationException("WRITE_DISABLED", "Main publication and workspace refresh require Write Commands ON.");
            return AdmitMainOperation(session, action, request, () =>
            {
                if (state.mainOperations.Count >= MaximumRetainedMainOperations || state.mainOperations.Count(item => !MainTerminal(item)) >= MaximumPendingMainOperations)
                    throw new ManagedIntegrationException("MAIN_OPERATION_LIMIT", "Main operation queue or retained history is full (four pending, 64 retained).");
                var operation = new ManagedMainOperationRecord
                {
                    id = "op-" + Guid.NewGuid().ToString("N"), sessionId = session.id, requestId = request.Value<string>("requestId"), kind = action == "integration_apply" ? "apply" : "refresh",
                    state = "queued", localAuthority = localAuthority, baselineBefore = session.integrationBaselineHash, created = Now, deadline = Now + 1200000, workOrder = state.nextWorkOrder++
                };
                if (operation.kind == "apply")
                {
                    operation.planId = Required(request, "planId", 64); operation.planHash = Required(request, "planHash", 64);
                    var plan = state.integrations.FirstOrDefault(item => item.id == operation.planId && item.sessionId == session.id);
                    if (plan == null || plan.state != "ready" || plan.planHash != operation.planHash) throw new ManagedIntegrationException("PLAN_NOT_READY", "Select an owned ready preview with its exact immutable plan hash.");
                    if (plan.baselineHash != session.integrationBaselineHash) throw new ManagedIntegrationException("STALE_BASELINE", "The reviewed baseline has advanced. Create a new preview.");
                }
                else
                {
                    ManagedSessionIntegration.BaselinePath(IntegrationDirectory(session), session.integrationBaselineHash);
                    operation.oldWorkspace = session.workspace; operation.newWorkspace = Path.Combine(root, "w-" + operation.id.Substring(3, 16));
                }
                return operation;
            });
        }

        string AdmissionPath(ManagedMainOperationRecord operation) => Path.Combine(registryRoot, "a-" + operation.id.Substring(3, 16) + ".json");
        static void WriteAdmissionMarker(string path, string content)
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(content);
            if (bytes.Length > 4096) throw new IOException("Acceptance marker exceeds its bound.");
            string temporary = path + ".tmp"; ManagedSessionFiles.CheckPathLength(temporary);
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        bool AdmissionValid(ManagedMainOperationRecord operation)
        {
            try
            {
                string path = AdmissionPath(operation);
                if (!File.Exists(path) || string.IsNullOrEmpty(operation.admissionHash) || new FileInfo(path).Length > 4096) return false;
                ManagedSessionFiles.RejectLink(path);
                return ManagedSessionFiles.Hash(File.ReadAllBytes(path)) == operation.admissionHash;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        JObject AdmitMainOperation(ManagedSessionRecord session, string action, JObject request, Func<ManagedMainOperationRecord> create)
        {
            string key = action + ":" + Required(request, "requestId", 128);
            if (session.receipts.TryGetValue(key, out string existing)) return JObject.Parse(existing);
            if (session.receipts.Count >= 256) throw new ManagedIntegrationException("RECEIPT_LIMIT", "Session receipt history is full.");
            var operation = create();
            operation.state = "admission_pending";
            string marker = JsonConvert.SerializeObject(new { operation.id, operation.sessionId, operation.requestId, operation.kind, operation.workOrder, operation.planHash });
            operation.admissionHash = ManagedSessionFiles.HashText(marker);
            var acknowledgement = MainOperationView(operation); acknowledgement["state"] = "queued";
            state.mainOperations.Add(operation); session.receipts.Add(key, acknowledgement.ToString(Formatting.None));
            bool stateSaved = false;
            try
            {
                // An atomic state save alone never makes a job runnable. Its separate
                // acceptance fence is created only after the receipt is durable.
                Save();
                using (var persisted = new FileStream(statePath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) persisted.Flush(true);
                stateSaved = true;
                admissionMarkerWriter(AdmissionPath(operation), marker);
                if (!AdmissionValid(operation)) throw new IOException("The durable Main operation acceptance marker could not be verified.");
                operation.state = "queued";
                return acknowledgement;
            }
            catch
            {
                // A writer may throw after publishing the valid marker. That is a proven
                // acceptance, so retain the stable acknowledgement instead of rejecting it.
                if (stateSaved && AdmissionValid(operation)) { operation.state = "queued"; return acknowledgement; }
                state.mainOperations.Remove(operation); session.receipts.Remove(key); persistencePending = true;
                // Work order gaps are intentional: never reuse a possibly durable sequence.
                throw;
            }
        }

        void TickMainOperations()
        {
            foreach (string id in applyPreparations.Where(item => item.Value.IsCompleted && MainTerminal(state.mainOperations.First(record => record.id == item.Key))).Select(item => item.Key).ToArray()) FinishPreparation(id);
            foreach (string id in refreshPreparations.Where(item => item.Value.IsCompleted && MainTerminal(state.mainOperations.First(record => record.id == item.Key))).Select(item => item.Key).ToArray()) FinishPreparation(id);
            foreach (string id in refreshVerifications.Where(item => item.Value.IsCompleted && MainTerminal(state.mainOperations.First(record => record.id == item.Key))).Select(item => item.Key).ToArray()) FinishPreparation(id);
            var operation = FirstMainOperation;
            if (operation == null) return;
            var session = state.sessions.First(item => item.id == operation.sessionId);
            try
            {
                if (!operation.sourceApplied && !AdmissionValid(operation)) throw new ManagedIntegrationException("ADMISSION_INTEGRITY", "This operation has no valid durable acceptance fence; no Main writes are permitted.");
                if (operation.state == "admission_pending") { operation.state = "queued"; Save(); }
                if (!operation.sourceApplied && operation.state != "applying")
                {
                    if (Now > operation.deadline) { FailMainOperation(operation, "MAIN_OPERATION_DEADLINE", "The bounded queue/preparation deadline expired."); return; }
                    if (!operation.localAuthority) RequirePreviewAuthority(session);
                    if (!MCPHandlers.AllowWrites) throw new ManagedIntegrationException("WRITE_DISABLED", "Write Commands were disabled before publication.");
                    if (session.integrationBaselineHash != operation.baselineBefore) throw new ManagedIntegrationException("STALE_BASELINE", "The session baseline changed while this operation waited.");
                }
                if (operation.state == "queued")
                {
                    if (EarlierWorkPending(operation.workOrder)) { operation.waitReason = "earlier_snapshot_or_build"; return; }
                    if (state.mainOperations.Any(item => item.state == "recovery_required")) { operation.waitReason = "recovery_required"; return; }
                    string dirty = MainBusyReason();
                    if (dirty != null) { operation.waitReason = dirty; return; }
                    operation.waitReason = null; operation.state = "preparing"; Save();
                    var source = new CancellationTokenSource(); cancellation.Add(operation.id, source);
                    if (operation.kind == "apply")
                    {
                        var plan = state.integrations.First(item => item.id == operation.planId);
                        applyPreparations.Add(operation.id, Task.Run(() =>
                        {
                            CheckRetainedStorage(source.Token);
                            return ManagedSessionIntegration.PrepareApply(mainProject, session.workspace, IntegrationDirectory(session), operation.baselineBefore,
                                IntegrationPlanDirectory(session, plan), operation.planHash, MainOperationDirectory(operation), source.Token);
                        }, source.Token));
                    }
                    else refreshPreparations.Add(operation.id, Task.Run(() =>
                    {
                        CheckRetainedStorage(source.Token);
                        if (!ManagedSessionIntegration.IsPristine(session.workspace, IntegrationDirectory(session), operation.baselineBefore, source.Token))
                            throw new ManagedIntegrationException("PRIVATE_CHANGES", "Refresh requires pristine private source. Preserve edits or create a new session; no source was discarded.");
                        return ManagedSessionIntegration.Snapshot(mainProject, operation.newWorkspace, IntegrationDirectory(session), source.Token);
                    }, source.Token));
                    return;
                }
                if (operation.state == "preparing")
                {
                    if (operation.kind == "apply")
                    {
                        var task = applyPreparations[operation.id]; if (!task.IsCompleted) return;
                        if (task.IsCanceled) throw new ManagedIntegrationException("PREPARATION_CANCELLED", "Preparation was cancelled without Main changes.");
                        if (task.Exception != null) throw task.Exception.GetBaseException();
                        operation.preparationHash = task.Result;
                    }
                    else
                    {
                        var task = refreshPreparations[operation.id]; if (!task.IsCompleted) return;
                        if (task.IsCanceled) throw new ManagedIntegrationException("PREPARATION_CANCELLED", "Refresh preparation was cancelled.");
                        if (task.Exception != null) throw task.Exception.GetBaseException();
                        operation.baselineAfter = task.Result.BaselineHash; operation.newRevision = task.Result.Revision;
                    }
                    FinishPreparation(operation.id);
                    operation.state = "prepared"; Save();
                }
                if (operation.state == "prepared")
                {
                    string busy = MainBusyReason(); if (busy != null) { operation.waitReason = busy; return; }
                    if (operation.kind == "refresh")
                    {
                        operation.state = "verifying"; Save();
                        var source = new CancellationTokenSource(); cancellation.Add(operation.id, source);
                        refreshVerifications.Add(operation.id, Task.Run(() =>
                        {
                            string before = ManagedSessionIntegration.WorkspaceMetadataStamp(session.workspace);
                            if (!ManagedSessionIntegration.IsPristine(session.workspace, IntegrationDirectory(session), operation.baselineBefore, source.Token) || before != ManagedSessionIntegration.WorkspaceMetadataStamp(session.workspace))
                                throw new ManagedIntegrationException("PRIVATE_CHANGES", "Private source changed during refresh verification. Both generations were preserved.");
                            return before;
                        }, source.Token));
                        return;
                    }
                    if (DirtyTargetAsset(operation)) { operation.waitReason = "dirty_main_asset"; return; }
                    operation.activeDeadline = Now + MainOperationGraceMilliseconds;
                    operation.state = "applying"; Save();
                    GrantMainOperationHeartbeat();
                    CommitMainOperation(session, operation);
                    return;
                }
                if (operation.state == "verifying")
                {
                    var task = refreshVerifications[operation.id]; if (!task.IsCompleted) return;
                    if (task.IsCanceled) throw new ManagedIntegrationException("PREPARATION_CANCELLED", "Refresh verification was cancelled.");
                    if (task.Exception != null) throw task.Exception.GetBaseException();
                    operation.verificationStamp = task.Result; FinishPreparation(operation.id);
                    if (session.workspace != operation.oldWorkspace || ManagedSessionIntegration.WorkspaceMetadataStamp(session.workspace) != operation.verificationStamp)
                        throw new ManagedIntegrationException("PRIVATE_CHANGES", "Private source changed after verification; the old source was retained.");
                    if (operation.baselineAfter == operation.baselineBefore)
                    {
                        operation.stagedWorkspace = operation.newWorkspace; operation.newWorkspace = session.workspace;
                        operation.state = "succeeded"; operation.compileResult = "workspace_unchanged"; Save(); return;
                    }
                    if (session.retainedWorkspaces.Count >= MaximumWorkspaceGenerations - 1) throw new ManagedIntegrationException("GENERATION_LIMIT", "Four activated workspace generations are retained; neither old nor staged source was discarded.");
                    session.retainedWorkspaces.Add(session.workspace); session.workspace = operation.newWorkspace;
                    session.integrationBaselineHash = operation.baselineAfter; session.baselineRevision = operation.newRevision;
                    operation.state = "succeeded"; operation.compileResult = "workspace_refreshed"; Save(); return;
                }
                if (operation.sourceApplied) TickMainValidation(operation);
            }
            catch (Exception error)
            {
                if (operation.sourceApplied) { operation.state = "validation_interrupted"; operation.failureCode = "VALIDATION_FAILED"; operation.failureDetail = error.Message; Save(); }
                else if (error is ManagedApplyFailure failure)
                { operation.state = failure.RecoveryRequired ? "recovery_required" : "rolled_back"; operation.failureCode = "PUBLICATION_FAILED"; operation.failureDetail = failure.Message; Save(); }
                else FailMainOperation(operation, (error as ManagedIntegrationException)?.Code ?? "MAIN_OPERATION_FAILED", error.Message);
            }
        }

        void CommitMainOperation(ManagedSessionRecord session, ManagedMainOperationRecord operation)
        {
            bool reload = false, refresh = false, editing = false;
            try
            {
                EditorApplication.LockReloadAssemblies(); reload = true;
                AssetDatabase.DisallowAutoRefresh(); refresh = true;
                AssetDatabase.StartAssetEditing(); editing = true;
                operation.baselineAfter = ManagedSessionIntegration.CommitApply(mainProject, session.workspace, IntegrationDirectory(session), MainOperationDirectory(operation), operation.preparationHash,
                    beforeFirstWrite: () => ValidateMainWriteAuthority(session, operation));
                session.integrationBaselineHash = operation.baselineAfter;
                operation.sourceApplied = true; operation.state = "sources_applied";
                operation.compileAttemptId = "compile-" + Guid.NewGuid().ToString("N");
                var plan = state.integrations.First(item => item.id == operation.planId);
                operation.compileRequested = ManagedSessionIntegration.ReadPlan(IntegrationPlanDirectory(session, plan), operation.planHash).entries.Any(item => CodePath(item.path));
                Save();
            }
            finally
            {
                try { if (editing) AssetDatabase.StopAssetEditing(); }
                finally { try { if (refresh) AssetDatabase.AllowAutoRefresh(); } finally { if (reload) EditorApplication.UnlockReloadAssemblies(); } }
            }
        }

        static bool CodePath(string path)
        {
            string asset = path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? path.Substring(0, path.Length - 5) : path;
            return new[] { ".cs", ".asmdef", ".asmref", ".dll", ".rsp" }.Any(extension => asset.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }
        internal void ValidateMainWriteAuthority(ManagedSessionRecord session, ManagedMainOperationRecord operation)
        {
            if (!operation.localAuthority) RequirePreviewAuthority(session);
            if (!MCPHandlers.AllowWrites) throw new ManagedIntegrationException("WRITE_DISABLED", "Write Commands were disabled before the first source write.");
            if (!AdmissionValid(operation)) throw new ManagedIntegrationException("ADMISSION_INTEGRITY", "The durable operation acceptance fence changed before publication.");
            if (session.integrationBaselineHash != operation.baselineBefore) throw new ManagedIntegrationException("STALE_BASELINE", "The session baseline advanced before publication.");
            if (Now > operation.deadline || Now > operation.activeDeadline) throw new ManagedIntegrationException("MAIN_OPERATION_DEADLINE", "The operation deadline expired before its first source write.");
        }
        bool DirtyTargetAsset(ManagedMainOperationRecord operation)
        {
            var record = state.integrations.First(item => item.id == operation.planId);
            var plan = ManagedSessionIntegration.ReadPlan(IntegrationPlanDirectory(state.sessions.First(item => item.id == operation.sessionId), record), operation.planHash);
            foreach (string path in plan.entries.Where(item => item.kind == "file").Select(item => item.path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? item.path.Substring(0, item.path.Length - 5) : item.path).Distinct())
                foreach (var asset in AssetDatabase.LoadAllAssetsAtPath(path)) if (asset != null && EditorUtility.IsDirty(asset)) return true;
            return false;
        }
        string MainBusyReason()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return "main_play_mode";
            if (EditorApplication.isCompiling) return "main_compiling";
            if (EditorApplication.isUpdating) return "main_importing";
            for (int index = 0; index < SceneManager.sceneCount; index++) if (SceneManager.GetSceneAt(index).isDirty) return "dirty_main_scene";
            return null;
        }

        void TickMainValidation(ManagedMainOperationRecord operation)
        {
            if (Now > operation.activeDeadline) { operation.state = "validation_interrupted"; operation.failureCode = "VALIDATION_DEADLINE"; operation.failureDetail = "Main source was applied, but import/compile did not finish before the bounded deadline."; Save(); return; }
            if (!operation.refreshIssued)
            {
                if (EditorApplication.isCompiling || EditorApplication.isUpdating) return;
                operation.state = "importing"; operation.refreshIssued = true; Save();
                AssetDatabase.Refresh();
                if (operation.compileRequested && !operation.compileStarted) CompilationPipeline.RequestScriptCompilation();
                return;
            }
            if (EditorApplication.isUpdating || EditorApplication.isCompiling) return;
            if (operation.compileRequested && !operation.compileFinished) return;
            operation.state = operation.errorCount == 0 ? "succeeded" : "compile_failed";
            operation.compileResult = operation.compileStarted ? (operation.errorCount == 0 ? "compiled" : "compile_failed") : "compile_not_required";
            Save();
        }

        internal void AttachCompilationCallbacks()
        {
            if (compilationAttached) return;
            CompilationPipeline.compilationStarted += OnMainCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished += OnMainAssemblyFinished;
            CompilationPipeline.compilationFinished += OnMainCompilationFinished;
            compilationAttached = true;
        }
        internal void DetachCompilationCallbacks()
        {
            if (!compilationAttached) return;
            CompilationPipeline.compilationStarted -= OnMainCompilationStarted;
            CompilationPipeline.assemblyCompilationFinished -= OnMainAssemblyFinished;
            CompilationPipeline.compilationFinished -= OnMainCompilationFinished;
            compilationAttached = false;
        }
        void OnMainCompilationStarted(object context)
        {
            var operation = FirstMainOperation;
            if (operation == null || !operation.sourceApplied) return;
            compileContext = context; contextOperationId = operation.id; operation.compileStarted = true; operation.state = "compiling"; Save();
        }
        void OnMainAssemblyFinished(string assembly, CompilerMessage[] messages)
        {
            var operation = state.mainOperations.FirstOrDefault(item => item.id == contextOperationId);
            if (operation == null || compileContext == null) return;
            foreach (var message in messages.Where(item => item.type == CompilerMessageType.Error))
            {
                operation.errorCount++;
                if (operation.diagnostics.Count < 100) operation.diagnostics.Add(new ManagedCompilationDiagnostic { assembly = assembly, message = message.message?.Substring(0, Math.Min(4096, message.message.Length)), file = message.file, line = message.line, column = message.column });
            }
            Save();
        }
        void OnMainCompilationFinished(object context)
        {
            if (!ReferenceEquals(context, compileContext)) return;
            var operation = state.mainOperations.FirstOrDefault(item => item.id == contextOperationId);
            if (operation != null) { operation.compileFinished = true; operation.compileResult = operation.errorCount == 0 ? "compiled" : "compile_failed"; Save(); }
            compileContext = null; contextOperationId = null;
        }

        internal long HeartbeatStamp(bool beforeReload)
        {
            long normal = Now + (beforeReload ? 60000 : 0);
            long active = state.mainOperations.Where(item => (item.sourceApplied && (!MainTerminal(item) || beforeReload)) || item.state == "applying").Select(item => item.activeDeadline).DefaultIfEmpty(0).Max();
            return Math.Max(normal, active);
        }
        void GrantMainOperationHeartbeat()
        { foreach (var run in state.runs.Where(Active)) ManagedSessionFiles.WriteAtomic(Path.Combine(run.mailbox, "supervisor-heartbeat"), HeartbeatStamp(false).ToString()); }

        void FinishPreparation(string id)
        {
            applyPreparations.Remove(id); refreshPreparations.Remove(id); refreshVerifications.Remove(id);
            if (cancellation.TryGetValue(id, out var source)) { source.Dispose(); cancellation.Remove(id); }
        }
        void FailMainOperation(ManagedMainOperationRecord operation, string code, string detail)
        {
            if (cancellation.TryGetValue(operation.id, out var source)) source.Cancel();
            operation.state = "failed"; operation.failureCode = code; operation.failureDetail = detail; Save();
        }
        void CancelPendingMainOperations(string sessionId, string reason)
        {
            foreach (var operation in state.mainOperations.Where(item => item.sessionId == sessionId && !item.sourceApplied && !MainTerminal(item)))
            {
                if (cancellation.TryGetValue(operation.id, out var source)) source.Cancel();
                operation.state = "cancelled"; operation.failureCode = "OWNER_CLOSED"; operation.failureDetail = reason;
            }
        }
        void CheckpointMainOperations(bool shutdown)
        {
            foreach (var operation in state.mainOperations.Where(item => !MainTerminal(item)))
            {
                if (operation.sourceApplied)
                {
                    if (operation.compileFinished) { operation.state = operation.errorCount == 0 ? "succeeded" : "compile_failed"; }
                    else { operation.state = "validation_interrupted"; operation.failureCode = shutdown ? "HOST_SHUTDOWN" : "DOMAIN_RELOAD"; operation.failureDetail = "Sources remain applied; validation completion was not observed."; }
                }
                else if (shutdown) { if (cancellation.TryGetValue(operation.id, out var source)) source.Cancel(); operation.state = "cancelled"; operation.failureCode = "HOST_SHUTDOWN"; }
            }
        }
        void RecoverMainOperations()
        {
            foreach (var operation in state.mainOperations.Where(item => !MainTerminal(item)))
            {
                // Journal truth wins over an older admission/queue registry checkpoint.
                // Recovering metadata never replays a Main source mutation.
                var committedJournal = operation.kind == "apply" ? ManagedSessionIntegration.InspectJournal(MainOperationDirectory(operation)) : null;
                if (committedJournal?.state == "sources_applied")
                {
                    var session = state.sessions.First(item => item.id == operation.sessionId);
                    ManagedSessionIntegration.ValidateStoredBaseline(IntegrationDirectory(session), committedJournal.baselineAfterHash);
                    if (session.integrationBaselineHash != operation.baselineBefore && session.integrationBaselineHash != committedJournal.baselineAfterHash)
                        throw new ManagedIntegrationException("RECOVERY_BASELINE_CONFLICT", "Published operation and session baseline pointers diverged; source was preserved.");
                    session.integrationBaselineHash = committedJournal.baselineAfterHash; operation.baselineAfter = committedJournal.baselineAfterHash; operation.sourceApplied = true;
                    operation.state = "validation_interrupted"; operation.failureCode = "RECOVERED_APPLIED_SOURCE"; operation.failureDetail = "Durable publication completed; registry truth was recovered without replaying source writes. Import/compile completion is unknown.";
                    continue;
                }
                if (committedJournal != null && committedJournal.entries.Any(item => item.attempted) && committedJournal.state != "rolled_back")
                { operation.state = "recovery_required"; operation.failureCode = "PUBLICATION_INTERRUPTED"; operation.failureDetail = "Partial publication is never replayed automatically; inspect durable preimages/postimages."; continue; }
                if (committedJournal?.state == "rolled_back")
                { operation.state = "rolled_back"; operation.failureCode = "PUBLICATION_ROLLED_BACK"; operation.failureDetail = committedJournal.error; continue; }
                if (operation.state == "queued")
                {
                    if (!AdmissionValid(operation)) { operation.state = "failed"; operation.failureCode = "ADMISSION_INTEGRITY"; }
                    continue;
                }
                if (operation.state == "admission_pending")
                {
                    if (AdmissionValid(operation)) operation.state = "queued";
                    else
                    {
                        operation.state = "failed"; operation.failureCode = "ADMISSION_INTERRUPTED"; operation.failureDetail = "No valid acceptance marker was committed. The operation cannot run.";
                        var owner = state.sessions.FirstOrDefault(item => item.id == operation.sessionId);
                        owner?.receipts.Remove((operation.kind == "apply" ? "integration_apply:" : "refresh_from_main:") + operation.requestId);
                    }
                }
                else if (operation.state == "applying")
                {
                    if (committedJournal != null && committedJournal.entries.All(item => !item.attempted)) { operation.state = "failed"; operation.failureCode = "PRECOMMIT_INTERRUPTED"; operation.failureDetail = "No source write intent was recorded before interruption."; }
                    else { operation.state = "recovery_required"; operation.failureCode = "PUBLICATION_INTERRUPTED"; operation.failureDetail = "Inspect durable preimages/postimages; partial publication is never replayed automatically."; }
                }
                else { operation.state = operation.sourceApplied ? "validation_interrupted" : "failed"; operation.failureCode = "OPERATION_INTERRUPTED"; operation.failureDetail = "Source and operation evidence were preserved."; }
            }
        }

        JObject MainOperationStatus(ManagedSessionRecord session, string id)
        {
            var operation = state.mainOperations.FirstOrDefault(item => item.id == id && item.sessionId == session.id);
            if (operation == null) throw new UnauthorizedAccessException("OWNER_DENIED: unknown Main operation.");
            return MainOperationView(operation);
        }
        JObject CancelMainOperation(ManagedSessionRecord session, JObject request)
        {
            string id = Required(request, "operationId", 64);
            var operation = state.mainOperations.FirstOrDefault(item => item.id == id && item.sessionId == session.id);
            if (operation == null) throw new UnauthorizedAccessException("OWNER_DENIED: unknown Main operation.");
            return Mutate(session, "integration_cancel", request, () =>
            {
                if (operation.sourceApplied || operation.state == "applying")
                {
                    var retained = MainOperationView(operation); retained["cancelAccepted"] = false;
                    retained["cancelReason"] = "Source publication has started or is already applied; changes remain retained and validation/recovery is reported truthfully.";
                    return retained;
                }
                if (!MainTerminal(operation))
                {
                    if (cancellation.TryGetValue(operation.id, out var source)) source.Cancel();
                    operation.state = "cancelled"; operation.failureCode = "OWNER_CANCELLED"; operation.failureDetail = "Cancelled before Main source writes; private source and staging evidence were preserved.";
                }
                var result = MainOperationView(operation); result["cancelAccepted"] = operation.state == "cancelled"; return result;
            });
        }
        static JObject MainOperationView(ManagedMainOperationRecord operation) => JObject.FromObject(new
        {
            operationId = operation.id, sessionId = operation.sessionId, operation.kind, operation.state, operation.waitReason, operation.sourceApplied,
            baselineBefore = operation.baselineBefore, baselineAfter = operation.baselineAfter, operation.planId, operation.planHash,
            operation.compileAttemptId, operation.compileStarted, operation.compileFinished, operation.compileResult, operation.errorCount,
            diagnostics = operation.diagnostics, diagnosticsTruncated = operation.errorCount > operation.diagnostics.Count,
            failureCode = operation.failureCode, failureDetail = operation.failureDetail,
            workspacePath = operation.newWorkspace, previousWorkspacePath = operation.oldWorkspace, stagedWorkspacePath = operation.stagedWorkspace, operation.deadline, operation.activeDeadline
        });

        internal JObject BeginLocalReview(string sessionId) => IntegrationCommand(state.sessions.First(item => item.id == sessionId), "integration_plan", new JObject { ["requestId"] = "local-" + Guid.NewGuid().ToString("N") }, true);
        internal JObject ReadLocalIntegration(string sessionId, string planId, string operationId = null, int offset = 0) => IntegrationCommand(state.sessions.First(item => item.id == sessionId), "integration_status", new JObject { ["planId"] = planId, ["operationId"] = operationId, ["offset"] = offset }, true);
        internal JObject ApplyLocalReview(string sessionId, string planId, string planHash) => IntegrationCommand(state.sessions.First(item => item.id == sessionId), "integration_apply", new JObject { ["planId"] = planId, ["planHash"] = planHash, ["requestId"] = "local-" + Guid.NewGuid().ToString("N") }, true);
        internal JObject RefreshLocalWorkspace(string sessionId) => IntegrationCommand(state.sessions.First(item => item.id == sessionId), "refresh_from_main", new JObject { ["requestId"] = "local-" + Guid.NewGuid().ToString("N") }, true);
        internal JObject CancelLocalOperation(string sessionId, string operationId) => IntegrationCommand(state.sessions.First(item => item.id == sessionId), "integration_cancel", new JObject { ["operationId"] = operationId, ["requestId"] = "local-" + Guid.NewGuid().ToString("N") }, true);
    }
}
