using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    internal static class ManagedSessionQualification
    {
        internal const string QualifiedVersion = "6000.0.75f1";
        internal const string ExcludedVersion = "6000.5.10f1";
        internal const string PendingReason = "Managed testing sessions require an interactive Windows Editor on a qualified Unity version. Automated batch supervision also requires the explicit -aiMcpManagedSupervisor flag; ordinary batch build workers do not initialize a managed host. Existing AI Unity MCP Server inspection/edit tools remain available.";
        internal static bool IsExplicitSupervisor(bool batchMode, string[] arguments) => batchMode && arguments != null && Array.IndexOf(arguments, "-aiMcpManagedSupervisor") >= 0;
        internal static bool IsEligibleHostMode(bool batchMode, string[] arguments) => !batchMode || IsExplicitSupervisor(batchMode, arguments);
        internal static string ActivationReason => IsEligibleHostMode(Application.isBatchMode, Environment.GetCommandLineArgs()) ? null : PendingReason;
        internal static string ExclusionReason(string unityVersion)
        {
            if (string.Equals(unityVersion, QualifiedVersion, StringComparison.Ordinal)) return null;
            if (string.Equals(unityVersion, ExcludedVersion, StringComparison.Ordinal))
                return "Managed testing sessions remain disabled for Unity 6000.5.10f1 after native failures during qualification. This version has not been requalified after the native stability fixes. Existing AI Unity MCP Server inspection/edit tools remain available.";
            return "Managed testing sessions are qualified only for Windows Editor on Unity " + QualifiedVersion + "; this version is not qualified. Existing AI Unity MCP Server inspection/edit tools remain available.";
        }
    }
    [Serializable] internal sealed class ManagedSessionRecord
    {
        public string id, label, capabilityHash, registrationRequestId, workspace, state, error, baselineRevision, integrationBaselineHash;
        public long created, leaseUntil;
        public long workOrder;
        public List<string> retainedWorkspaces = new List<string>();
        public int leaseSeconds;
        public List<ManagedEvent> events = new List<ManagedEvent>();
        public long nextSequence = 1;
        public Dictionary<string, string> receipts = new Dictionary<string, string>();
    }
    [Serializable] internal sealed class ManagedEvent
    {
        public long sequence, time;
        public string type, reason, runId, jobId;
    }
    [Serializable] internal sealed class ManagedBuildRecord
    {
        public string id, sessionId, requestId, state, project, output, revision, error;
        public int pid;
        public long processStart, created, deadline, workOrder;
    }
    [Serializable] internal sealed class ManagedRunRecord
    {
        public string id, sessionId, jobId, state, mailbox, capability, reason, executable;
        public int pid, width, height;
        public long processStart, created, deadline, closeDeadline;
        public bool terminalEvent;
        public List<string> commandIds = new List<string>();
        public Dictionary<string, long> commandDeadlines = new Dictionary<string, long>();
    }
    [Serializable] internal sealed class ManagedSessionState
    {
        public int schemaVersion = 1;
        public List<ManagedSessionRecord> sessions = new List<ManagedSessionRecord>();
        public List<ManagedBuildRecord> builds = new List<ManagedBuildRecord>();
        public List<ManagedRunRecord> runs = new List<ManagedRunRecord>();
        public List<ManagedIntegrationRecord> integrations = new List<ManagedIntegrationRecord>();
        public List<ManagedMainOperationRecord> mainOperations = new List<ManagedMainOperationRecord>();
        public long nextWorkOrder = 1;
    }

    [InitializeOnLoad]
    internal static class ManagedSessionHost
    {
        internal static ManagedSessionManager Manager { get; private set; }
        static ManagedSessionHost()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess() || ManagedSessionQualification.ActivationReason != null) return;
            EditorApplication.delayCall += Initialize;
        }
        internal static void Initialize()
        {
            if (Manager != null) return;
            if (ManagedSessionQualification.ActivationReason != null) return;
            if (Application.platform != RuntimePlatform.WindowsEditor) return;
            if (ManagedSessionQualification.ExclusionReason(Application.unityVersion) != null) return;
            try
            {
                Manager = new ManagedSessionManager(MCPPackagePaths.ProjectRoot(), EditorApplication.applicationPath, Application.unityVersion);
                Manager.AttachCompilationCallbacks();
                EditorApplication.update += Manager.Tick;
                AssemblyReloadEvents.beforeAssemblyReload += Manager.BeforeReload;
                EditorApplication.quitting += Manager.Shutdown;
            }
            catch (Exception exception) { UnityEngine.Debug.LogError("[AI Unity MCP Server] Managed session recovery failed: " + exception.Message); }
        }
    }

    internal sealed partial class ManagedSessionManager
    {
        internal const int MaximumActiveSessions = 2;
        readonly string mainProject, editorExecutable, unityVersion, root, registryRoot, statePath;
        readonly ManagedSessionState state;
        readonly Dictionary<string, Task<ManagedSnapshotResult>> copies = new Dictionary<string, Task<ManagedSnapshotResult>>();
        readonly Dictionary<string, CancellationTokenSource> cancellation = new Dictionary<string, CancellationTokenSource>();
        double nextTick;
        double nextWarning;
        bool persistencePending;
        internal IReadOnlyList<ManagedSessionRecord> Sessions => state.sessions;
        internal IReadOnlyList<ManagedRunRecord> Runs => state.runs;
        internal IReadOnlyList<ManagedBuildRecord> Builds => state.builds;
        internal string Root => root;
        static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        internal ManagedSessionManager(string mainProject, string editorExecutable, string unityVersion, string storageRoot = null, Action<string, string> admissionMarkerWriter = null)
        {
            this.admissionMarkerWriter = admissionMarkerWriter ?? new Action<string, string>(WriteAdmissionMarker);
            this.mainProject = Path.GetFullPath(mainProject);
            this.editorExecutable = editorExecutable;
            this.unityVersion = unityVersion;
            registryRoot = storageRoot ?? Path.Combine(mainProject, "Library", "AIUnityMCPServer", "managed-sessions");
            root = storageRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIUnityMCPServer", "s", ManagedSessionFiles.HashText(this.mainProject.ToLowerInvariant()).Substring(0, 12));
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(registryRoot);
            ManagedSessionFiles.RejectLink(root);
            ManagedSessionFiles.RejectLink(registryRoot);
            ManagedSessionFiles.RestrictDirectory(root);
            if (root != registryRoot) ManagedSessionFiles.RestrictDirectory(registryRoot);
            statePath = Path.Combine(registryRoot, "registry.json");
            state = File.Exists(statePath) ? JsonConvert.DeserializeObject<ManagedSessionState>(File.ReadAllText(statePath)) : new ManagedSessionState();
            if (state == null || state.schemaVersion != 1) throw new InvalidDataException("Unsupported managed registry schema.");
            foreach (var session in state.sessions.Where(item => item.state == "snapshotting"))
            { session.state = "failed"; session.error = "Snapshot interrupted by Editor reload; create a new session. Private files were preserved."; }
            foreach (var build in state.builds.Where(item => item.state == "snapshotting"))
            { build.state = "failed"; build.error = "Build snapshot interrupted by Editor reload; retry build."; }
            RecoverIntegrationPlans();
            RecoverMainOperations();
            Save();
        }

        internal JObject Execute(string group, JObject request)
        {
            try
            {
                string action = Required(request, "action", 32);
                if (group == "session" && action == "register") return Register(request);
                ManagedSessionRecord session = Authorize(request);
                switch (group)
                {
                    case "session": return SessionCommand(session, action, request);
                    case "workspace": return WorkspaceCommand(session, action, request);
                    case "build": return BuildCommand(session, action, request);
                    case "player": return PlayerCommand(session, action, request);
                    default: return Error("INVALID_GROUP", "Unknown managed operation.");
                }
            }
            catch (ManagedIntegrationException exception) { return Error(exception.Code, exception.Message); }
            catch (Exception exception) when (exception is ArgumentException || exception is InvalidOperationException || exception is IOException || exception is UnauthorizedAccessException || exception is JsonException)
            {
                string code = exception is UnauthorizedAccessException ? "OWNER_DENIED" : "MANAGED_REQUEST_FAILED";
                return Error(code, exception.Message);
            }
        }

        internal static JObject Error(string code, string message) => new JObject { ["error"] = message, ["code"] = code };
        static string Required(JObject request, string key, int maximum)
        {
            string value = request.Value<string>(key);
            if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("Invalid or missing " + key + ".");
            return value;
        }
        static bool Active(ManagedSessionRecord session) => session.state != "closed" && session.state != "expired" && session.state != "failed";
        static bool Active(ManagedRunRecord run) => run.state == "starting" || run.state == "running" || run.state == "closing";
        static bool Active(ManagedBuildRecord build) => build.state == "snapshot_queued" || build.state == "snapshotting" || build.state == "queued" || build.state == "building" || build.state == "cancelling";
        internal static bool RequiresWrites(string group, string action) =>
            group == "session" ? action == "register" || action == "renew" :
            group == "workspace" ? action == "write" || action == "integration_apply" || action == "refresh_from_main" :
            group == "build" ? action == "start" : group == "player" && (action == "start" || action == "input");

        ManagedSessionRecord Authorize(JObject request)
        {
            string id = Required(request, "sessionId", 64);
            string capability = Required(request, "capability", 128);
            var session = state.sessions.FirstOrDefault(item => item.id == id);
            if (session == null || !ManagedSessionFiles.CapabilityMatches(capability, session.capabilityHash)) throw new UnauthorizedAccessException("OWNER_DENIED: session capability does not match.");
            return session;
        }

        JObject Register(JObject request)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) return Error("PLATFORM_UNSUPPORTED", "Managed Players v1 require the Windows Editor.");
            string requestId = Required(request, "requestId", 128);
            if (state.sessions.Any(item => item.registrationRequestId == requestId)) return Error("CAPABILITY_ALREADY_ISSUED", "Registration was already accepted. Use the original capability or close the session in the dashboard.");
            if (state.sessions.Count(item => Active(item)) >= MaximumActiveSessions) return Error("SESSION_LIMIT", "Two sessions are already active. Close one before registering another.");
            if (state.sessions.Count >= 16) return Error("RETENTION_LIMIT", "Sixteen sessions are retained. Export private source and explicitly clean the managed storage before registering more.");
            CheckDiskBudget();
            string capability = ManagedSessionFiles.NewCapability();
            string id = "session-" + Guid.NewGuid().ToString("N");
            int lease = Math.Max(60, Math.Min(7200, request.Value<int?>("leaseSeconds") ?? 1800));
            var session = new ManagedSessionRecord
            {
                id = id, label = Required(request, "label", 80), capabilityHash = ManagedSessionFiles.HashText(capability), registrationRequestId = requestId,
                workspace = Path.Combine(root, "w-" + id.Substring(8, 16)), state = "queued", created = Now, leaseSeconds = lease, leaseUntil = Now + lease * 1000L, workOrder = state.nextWorkOrder++
            };
            state.sessions.Add(session);
            StartOrderedCopies();
            AddEvent(session, "session_registered", "snapshotting");
            Save();
            JObject response = SessionView(session);
            response["capability"] = capability;
            response["instruction"] = "Keep this capability private. Wait for state=ready, then edit only workspacePath. Main and other sessions are never synchronized automatically.";
            return response;
        }

        JObject SessionCommand(ManagedSessionRecord session, string action, JObject request)
        {
            if (action == "status") return SessionView(session);
            if (action == "events")
            {
                long after = request.Value<long?>("afterSequence") ?? 0;
                return new JObject { ["sessionId"] = session.id, ["cursorExpired"] = session.events.Count > 0 && after < session.events[0].sequence - 1,
                    ["events"] = JArray.FromObject(session.events.Where(item => item.sequence > after)), ["nextSequence"] = session.nextSequence - 1 };
            }
            if (action == "close") { CloseSession(session, "owner_closed"); return SessionView(session); }
            RequireActive(session);
            if (action == "renew")
            { session.leaseUntil = Now + session.leaseSeconds * 1000L; Save(); return SessionView(session); }
            return Error("INVALID_ACTION", "Unsupported session action.");
        }

        static void RequireActive(ManagedSessionRecord session)
        { if (!Active(session)) throw new InvalidOperationException("SESSION_CLOSED: private source is retained, but this session cannot start new work."); }
        static void RequireReady(ManagedSessionRecord session)
        { RequireActive(session); if (session.state != "ready") throw new InvalidOperationException("WORKSPACE_NOT_READY: wait for the saved-source snapshot."); }

        JObject WorkspaceCommand(ManagedSessionRecord session, string action, JObject request)
        {
            if (action == "status") return SessionView(session);
            if (action == "integration_plan" || action == "integration_status" || action == "integration_apply" || action == "integration_cancel" || action == "refresh_from_main") return IntegrationCommand(session, action, request);
            RequireReady(session);
            if (action == "changes")
            {
                // Inventory is deliberately bounded; export means the retained private path, never a main-project merge.
                string assets = Path.Combine(session.workspace, "Assets");
                var files = Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories).Take(501).Select(item => item.Substring(session.workspace.Length + 1).Replace('\\', '/')).ToArray();
                return new JObject { ["workspacePath"] = session.workspace, ["files"] = new JArray(files.Take(500)), ["truncated"] = files.Length > 500,
                    ["integration"] = "Private workspace only. Source integration into main requires a separate explicit operation with conflict checks." };
            }
            string path = ManagedSessionFiles.AssetPath(session.workspace, Required(request, "path", 512));
            if (action == "read")
            {
                if (!File.Exists(path)) return new JObject { ["path"] = request["path"], ["hash"] = "missing" };
                if (new FileInfo(path).Length > 262144) return Error("FILE_LIMIT", "Text reads are limited to 256 KiB.");
                byte[] bytes = File.ReadAllBytes(path);
                return new JObject { ["path"] = request["path"], ["hash"] = ManagedSessionFiles.Hash(bytes), ["content"] = new UTF8Encoding(false, true).GetString(bytes) };
            }
            if (action != "write") return Error("INVALID_ACTION", "Unsupported workspace action.");
            if (state.mainOperations.Any(item => item.sessionId == session.id && !MainTerminal(item))) return Error("WORKSPACE_BUSY", "Private writes are leased while a reviewed Main operation or refresh is pending.");
            if (state.builds.Any(item => item.sessionId == session.id && Active(item))) return Error("WORKSPACE_BUSY", "Managed writes are locked while a build snapshot/import/build is active.");
            return Mutate(session, "write", request, () =>
            {
                string content = request.Value<string>("content") ?? throw new ArgumentException("content is required.");
                if (System.Text.Encoding.UTF8.GetByteCount(content) > 262144) return Error("FILE_LIMIT", "Writes are limited to 256 KiB.");
                string actual = File.Exists(path) ? ManagedSessionFiles.Hash(File.ReadAllBytes(path)) : "missing";
                if (actual != Required(request, "expectedHash", 64)) return Error("HASH_MISMATCH", "The private file changed. Read it again before writing.");
                ManagedSessionFiles.WriteAtomic(path, content);
                return new JObject { ["path"] = request["path"], ["hash"] = ManagedSessionFiles.HashText(content), ["workspacePath"] = session.workspace };
            });
        }

        JObject Mutate(ManagedSessionRecord session, string operation, JObject request, Func<JObject> action)
        {
            string key = operation + ":" + Required(request, "requestId", 128);
            if (session.receipts.TryGetValue(key, out string receipt)) return JObject.Parse(receipt);
            if (session.receipts.Count >= 256) return Error("RECEIPT_LIMIT", "Session mutation receipt budget is exhausted. Close it and create a new session.");
            JObject result = action();
            session.receipts.Add(key, result.ToString(Formatting.None));
            Save();
            return result;
        }

        JObject BuildCommand(ManagedSessionRecord session, string action, JObject request)
        {
            if (action == "status")
            {
                string id = request.Value<string>("jobId");
                return new JObject { ["builds"] = new JArray(state.builds.Where(item => item.sessionId == session.id && (string.IsNullOrEmpty(id) || item.id == id)).Select(BuildView)) };
            }
            if (action == "cancel")
            {
                var build = OwnedBuild(session, Required(request, "jobId", 64));
                CancelBuild(build, "owner_cancelled"); Save(); return BuildView(build);
            }
            RequireReady(session);
            if (action != "start") return Error("INVALID_ACTION", "Unsupported build action.");
            return Mutate(session, "build", request, () =>
            {
                if (state.builds.Any(item => item.sessionId == session.id && Active(item))) return Error("BUILD_PENDING", "This session already has a pending build.");
                if (state.builds.Count(item => item.sessionId == session.id) >= 4) return Error("ARTIFACT_LIMIT", "Four build records are retained per session. Export source and create a fresh session to continue.");
                CheckDiskBudget();
                string id = "build-" + Guid.NewGuid().ToString("N");
                string directory = Path.Combine(root, "b-" + id.Substring(6, 16));
                var build = new ManagedBuildRecord { id = id, sessionId = session.id, requestId = request.Value<string>("requestId"), state = "snapshot_queued",
                    project = Path.Combine(directory, "project"), output = Path.Combine(directory, "artifact", "Game.exe"), created = Now, deadline = Now + 1200000, workOrder = state.nextWorkOrder++ };
                state.builds.Add(build);
                StartOrderedCopies();
                AddEvent(session, "build_queued", "snapshotting", jobId: id);
                return BuildView(build);
            });
        }

        ManagedBuildRecord OwnedBuild(ManagedSessionRecord session, string id) => state.builds.FirstOrDefault(item => item.id == id && item.sessionId == session.id) ?? throw new UnauthorizedAccessException("OWNER_DENIED: unknown build.");
        ManagedRunRecord OwnedRun(ManagedSessionRecord session, string id) => state.runs.FirstOrDefault(item => item.id == id && item.sessionId == session.id) ?? throw new UnauthorizedAccessException("OWNER_DENIED: unknown Player.");

        JObject PlayerCommand(ManagedSessionRecord session, string action, JObject request)
        {
            if (action == "status")
            {
                string id = request.Value<string>("runId");
                var response = new JObject { ["runs"] = new JArray(state.runs.Where(item => item.sessionId == session.id && (string.IsNullOrEmpty(id) || item.id == id)).Select(RunView)) };
                string commandId = request.Value<string>("commandId");
                if (!string.IsNullOrEmpty(commandId))
                {
                    var run = OwnedRun(session, Required(request, "runId", 64));
                    if (!run.commandIds.Contains(commandId)) return Error("OWNER_DENIED", "Unknown Player command.");
                    string resultPath = Path.Combine(run.mailbox, commandId + ".result.json");
                    response["command"] = File.Exists(resultPath) ?
                        (TryReadBoundedJson(resultPath, out JObject commandResult) ? commandResult :
                            new JObject { ["state"] = "result_unavailable", ["commandId"] = commandId, ["nextAction"] = "Retry status for this commandId; do not replay the operation." }) :
                        run.commandDeadlines.TryGetValue(commandId, out long deadline) && Now > deadline ?
                            new JObject { ["state"] = "outcome_unknown", ["commandId"] = commandId, ["error"] = "COMMAND_DEADLINE: no result arrived; inspect Player state before retrying.", ["retryable"] = false } :
                            new JObject { ["state"] = "pending", ["commandId"] = commandId };
                }
                return response;
            }
            if (action == "close")
            { var existing = OwnedRun(session, Required(request, "runId", 64)); CloseRun(existing, "owner_closed"); Save(); return RunView(existing); }
            RequireReady(session);
            if (action == "start") return Mutate(session, "start", request, () => Launch(session, OwnedBuild(session, Required(request, "jobId", 64)), request));
            var runRecord = OwnedRun(session, Required(request, "runId", 64));
            if (runRecord.state != "running") return Error("PLAYER_NOT_READY", "Wait for the Player companion ready state.");
            if (action == "capture" || action == "input") return Mutate(session, action, request, () =>
            {
                if (runRecord.commandIds.Count >= 64) return Error("COMMAND_LIMIT", "This run has reached its retained command budget. Start another run.");
                string commandId = "command-" + Guid.NewGuid().ToString("N");
                var command = new JObject { ["schemaVersion"] = 1, ["id"] = commandId, ["capability"] = runRecord.capability, ["action"] = action,
                    ["name"] = request.Value<string>("inputName") ?? "", ["value"] = request.Value<string>("inputValue") ?? "", ["deadline"] = Now + 15000 };
                string payload = command.ToString();
                if (Encoding.UTF8.GetByteCount(payload) > 65536) return Error("COMMAND_LIMIT", "Serialized Player command exceeds the 64 KiB mailbox budget.");
                ManagedSessionFiles.WriteAtomic(Path.Combine(runRecord.mailbox, commandId + ".request.json"), payload);
                runRecord.commandIds.Add(commandId);
                runRecord.commandDeadlines[commandId] = command.Value<long>("deadline");
                return new JObject { ["state"] = "pending", ["runId"] = runRecord.id, ["commandId"] = commandId };
            });
            return Error("INVALID_ACTION", "Unsupported Player action.");
        }

        JObject Launch(ManagedSessionRecord session, ManagedBuildRecord build, JObject request)
        {
            if (build.state != "succeeded" || !File.Exists(build.output)) return Error("BUILD_NOT_READY", "A successful immutable Windows build is required.");
            if (state.runs.Any(item => item.sessionId == session.id && Active(item))) return Error("PLAYER_LIMIT", "Close this session's current Player first.");
            if (state.runs.Count(item => item.sessionId == session.id) >= 8) return Error("RUN_LIMIT", "Eight run records are retained per session.");
            string id = "run-" + Guid.NewGuid().ToString("N");
            string mailbox = Path.Combine(root, "r-" + id.Substring(4, 16));
            Directory.CreateDirectory(mailbox);
            var run = new ManagedRunRecord { id = id, sessionId = session.id, jobId = build.id, state = "starting", mailbox = mailbox,
                capability = ManagedSessionFiles.NewCapability(), executable = build.output, created = Now, deadline = Now + 60000,
                width = Math.Max(320, Math.Min(1280, request.Value<int?>("width") ?? 640)), height = Math.Max(180, Math.Min(720, request.Value<int?>("height") ?? 360)) };
            string configuration = Path.Combine(mailbox, "run-config.json");
            ManagedSessionFiles.WriteAtomic(configuration, new JObject { ["schemaVersion"] = 1, ["runId"] = id, ["owner"] = session.label,
                ["mailbox"] = mailbox, ["capability"] = run.capability, ["saveDirectory"] = Path.Combine(mailbox, "saves"), ["frameRate"] = 30 }.ToString());
            ManagedSessionFiles.WriteAtomic(Path.Combine(mailbox, "supervisor-heartbeat"), Now.ToString());
            using (Process process = StartProcess(build.output, new[] { "-screen-fullscreen", "0", "-screen-width", run.width.ToString(), "-screen-height", run.height.ToString(),
                "-logFile", Path.Combine(mailbox, "Player.log"), "-aiMcpRunConfig", configuration }, false))
            { run.pid = process.Id; run.processStart = process.StartTime.ToUniversalTime().Ticks; }
            state.runs.Add(run);
            AddEvent(session, "player_starting", "launch", id, build.id);
            return RunView(run);
        }

        internal void Tick()
        {
            if (EditorApplication.timeSinceStartup < nextTick) return;
            nextTick = EditorApplication.timeSinceStartup + 0.5;
            try
            {
                string before = JsonConvert.SerializeObject(state);
                CompleteCopies();
                CompleteIntegrationPlans();
                StartOrderedCopies();
                foreach (var session in state.sessions.Where(item => Active(item) && item.leaseUntil <= Now).ToArray()) CloseSession(session, "lease_expired");
                foreach (var build in state.builds.Where(item => item.state == "building" || item.state == "cancelling").ToArray()) PollBuild(build);
                if (!state.builds.Any(item => item.state == "building" || item.state == "cancelling"))
                {
                    var next = state.builds.FirstOrDefault(Active);
                    if (next != null && next.state == "queued" && CanStartWork(next.workOrder)) StartBuild(next);
                }
                TickMainOperations();
                foreach (var run in state.runs.Where(Active).ToArray()) PollRun(run);
                if (persistencePending || before != JsonConvert.SerializeObject(state)) Save();
            }
            catch (Exception exception)
            {
                if (EditorApplication.timeSinceStartup >= nextWarning)
                {
                    nextWarning = EditorApplication.timeSinceStartup + 30;
                    UnityEngine.Debug.LogError("[AI Unity MCP Server] Managed session update failed: " + exception.Message);
                }
            }
        }

        void StartCopy(string id, Func<CancellationToken, ManagedSnapshotResult> copy)
        {
            var source = new CancellationTokenSource();
            cancellation[id] = source;
            copies[id] = Task.Run(() => { CheckRetainedStorage(source.Token); return copy(source.Token); }, source.Token);
        }

        void CompleteCopies()
        {
            foreach (string id in copies.Keys.ToArray())
            {
                Task<ManagedSnapshotResult> task = copies[id];
                if (!task.IsCompleted) continue;
                copies.Remove(id); cancellation[id].Dispose(); cancellation.Remove(id);
                string error = task.IsCanceled ? "Snapshot cancelled." : task.Exception?.GetBaseException().Message;
                var session = state.sessions.FirstOrDefault(item => item.id == id);
                if (session != null)
                {
                    if (session.state != "snapshotting") continue;
                    session.state = error == null ? "ready" : "failed"; session.error = error; session.baselineRevision = error == null ? task.Result.Revision : "";
                    session.integrationBaselineHash = error == null ? task.Result.BaselineHash : null;
                    AddEvent(session, error == null ? "workspace_ready" : "workspace_failed", error ?? "saved_source_snapshot");
                }
                else
                {
                    var build = state.builds.First(item => item.id == id);
                    if (build.state != "snapshotting") continue;
                    build.state = error == null ? "queued" : "failed"; build.error = error; build.revision = error == null ? task.Result.Revision : "";
                }
            }
        }

        void StartBuild(ManagedBuildRecord build)
        {
            try
            {
                if (Now > build.deadline) { CancelBuild(build, "deadline"); return; }
                string configPath = Path.Combine(Path.GetDirectoryName(build.project), "build-config.json");
                ManagedSessionFiles.WriteAtomic(configPath, new JObject { ["schemaVersion"] = 1, ["output"] = build.output,
                    ["resultPath"] = Path.Combine(Path.GetDirectoryName(build.project), "build-result.json"), ["revision"] = build.revision, ["unityVersion"] = unityVersion }.ToString());
                using (Process process = StartProcess(editorExecutable, new[] { "-batchmode", "-nographics", "-projectPath", build.project, "-buildTarget", "Win64",
                    "-executeMethod", "AIUnityMCPServer.ManagedPlayerBuildWorker.Run", "-aiMcpBuildConfig", configPath, "-logFile", Path.Combine(Path.GetDirectoryName(build.project), "worker.log") }, true))
                { build.pid = process.Id; build.processStart = process.StartTime.ToUniversalTime().Ticks; }
                build.state = "building";
            }
            catch (Exception exception) { build.state = "failed"; build.error = exception.Message; }
        }

        void PollBuild(ManagedBuildRecord build)
        {
            if (build.state == "cancelling")
            { if (!IsOwnedProcessAlive(build.pid, build.processStart, editorExecutable)) build.state = "cancelled"; return; }
            if (Now > build.deadline) { CancelBuild(build, "deadline"); return; }
            if (IsOwnedProcessAlive(build.pid, build.processStart, editorExecutable)) return;
            string resultPath = Path.Combine(Path.GetDirectoryName(build.project), "build-result.json");
            JObject result = File.Exists(resultPath) ? ReadBoundedJson(resultPath) : null;
            bool success = result?.Value<bool?>("success") == true && result.Value<string>("revision") == build.revision && File.Exists(build.output);
            build.state = success ? "succeeded" : "failed";
            build.error = success ? "" : result?.Value<string>("error") ?? "Worker exited without a verified successful result. Inspect worker.log for compilation, package or license errors.";
            AddEvent(state.sessions.First(item => item.id == build.sessionId), success ? "build_succeeded" : "build_failed", build.error, jobId: build.id);
        }

        void PollRun(ManagedRunRecord run)
        {
            var session = state.sessions.First(item => item.id == run.sessionId);
            string executable = run.executable ?? state.builds.First(item => item.id == run.jobId).output;
            if (!IsOwnedProcessAlive(run.pid, run.processStart, executable))
            {
                string exitFile = Path.Combine(run.mailbox, "exit.json");
                string reported = File.Exists(exitFile) ? ReadBoundedJson(exitFile).Value<string>("reason") : null;
                string reason = run.reason ?? (reported == "window_closed" || reported == "supervisor_lost" ? reported : "process_exited");
                run.state = "closed"; run.reason = reason;
                if (!run.terminalEvent) { AddEvent(session, "player_closed", reason, run.id, run.jobId); run.terminalEvent = true; }
                return;
            }
            if (run.state == "closing")
            { if (Now >= run.closeDeadline) StopOwnedProcess(run.pid, run.processStart, executable); return; }
            ManagedSessionFiles.WriteAtomic(Path.Combine(run.mailbox, "supervisor-heartbeat"), HeartbeatStamp(false).ToString());
            string readyPath = Path.Combine(run.mailbox, "ready.json");
            if (run.state == "starting" && File.Exists(readyPath))
            {
                JObject ready = ReadBoundedJson(readyPath);
                if (ready.Value<string>("runId") == run.id) { run.state = "running"; AddEvent(session, "player_ready", "companion_connected", run.id, run.jobId); }
            }
            if (run.state == "starting" && Now > run.deadline) CloseRun(run, "startup_timeout");
        }

        internal void CloseRun(ManagedRunRecord run, string reason)
        {
            if (!Active(run) || run.state == "closing") return;
            run.reason = reason; run.state = "closing"; run.closeDeadline = Now + 5000;
            ManagedSessionFiles.WriteAtomic(Path.Combine(run.mailbox, "close.request.json"), new JObject { ["action"] = "quit", ["capability"] = run.capability, ["reason"] = reason }.ToString());
            Save();
        }

        internal void CloseSession(ManagedSessionRecord session, string reason)
        {
            CancelPendingMainOperations(session.id, reason);
            if (!Active(session)) return;
            if (cancellation.TryGetValue(session.id, out var source)) source.Cancel();
            foreach (var build in state.builds.Where(item => item.sessionId == session.id && Active(item)).ToArray()) CancelBuild(build, reason);
            foreach (var run in state.runs.Where(item => item.sessionId == session.id && Active(item)).ToArray()) CloseRun(run, reason);
            session.state = reason == "lease_expired" ? "expired" : "closed";
            AddEvent(session, "session_closed", reason); Save();
        }

        void CancelBuild(ManagedBuildRecord build, string reason)
        {
            if (!Active(build)) return;
            if (cancellation.TryGetValue(build.id, out var source)) source.Cancel();
            bool running = build.state == "building" || build.state == "cancelling";
            if (running) StopOwnedProcess(build.pid, build.processStart, editorExecutable);
            build.state = running && IsOwnedProcessAlive(build.pid, build.processStart, editorExecutable) ? "cancelling" : "cancelled"; build.error = reason;
            AddEvent(state.sessions.First(item => item.id == build.sessionId), "build_cancelled", reason, jobId: build.id);
        }

        internal void BeforeReload()
        {
            CheckpointMainOperations(false);
            DetachCompilationCallbacks();
            foreach (var source in cancellation.Values) source.Cancel();
            // Players self-exit if this grace heartbeat is not renewed after reload.
            foreach (var run in state.runs.Where(Active)) ManagedSessionFiles.WriteAtomic(Path.Combine(run.mailbox, "supervisor-heartbeat"), HeartbeatStamp(true).ToString());
            Save();
        }
        internal void Shutdown()
        {
            CheckpointMainOperations(true);
            DetachCompilationCallbacks();
            CancelIntegrationPlans();
            foreach (var session in state.sessions.Where(Active).ToArray()) CloseSession(session, "editor_shutdown");
            foreach (var build in state.builds.Where(Active).ToArray()) CancelBuild(build, "editor_shutdown");
            Save();
        }

        static Process StartProcess(string executable, IEnumerable<string> arguments, bool hidden)
        {
            var info = new ProcessStartInfo(executable, string.Join(" ", arguments.Select(Quote)))
            { UseShellExecute = false, CreateNoWindow = hidden, WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal, WorkingDirectory = Path.GetDirectoryName(executable) };
            return Process.Start(info) ?? throw new InvalidOperationException("Process start returned no process.");
        }
        static string Quote(string value) => "\"" + value.Replace("\"", "\\\"").TrimEnd('\\') + "\"";
        internal static bool IsOwnedProcessAlive(int pid, long start, string expectedExecutable)
        {
            if (pid <= 0 || start <= 0 || string.IsNullOrWhiteSpace(expectedExecutable)) return false;
            try
            {
                using (var process = Process.GetProcessById(pid))
                    return IsOwnedProcessAlive(process, start, expectedExecutable);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception exception) when (ProcessUnavailable(exception)) { return false; }
            catch (IOException) { return false; }
            catch (NotSupportedException) { return false; }
        }
        static bool IsOwnedProcessAlive(Process process, long start, string expectedExecutable) =>
            !process.HasExited && process.StartTime.ToUniversalTime().Ticks == start && ManagedProcessImage.Matches(process.Id, expectedExecutable);
        static bool ProcessUnavailable(System.ComponentModel.Win32Exception exception) =>
            exception.NativeErrorCode == 5 || exception.NativeErrorCode == 87 || exception.NativeErrorCode == 1168;
        static void StopOwnedProcess(int pid, long start, string expectedExecutable)
        {
            if (pid <= 0 || start <= 0 || string.IsNullOrWhiteSpace(expectedExecutable)) return;
            try
            {
                // Validate the same wrapper used for the action; never act on a fresh PID lookup.
                using (var process = Process.GetProcessById(pid))
                    if (IsOwnedProcessAlive(process, start, expectedExecutable)) process.Kill();
            }
            // Natural exit and access denial are expected races. Polling verifies the outcome;
            // unexpected native failures (including a failed CloseHandle) still propagate.
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            catch (System.ComponentModel.Win32Exception exception) when (ProcessUnavailable(exception)) { return; }
        }
        void CheckDiskBudget()
        {
            if (new DriveInfo(Path.GetPathRoot(root)).AvailableFreeSpace < 1024L * 1024 * 1024)
                throw new InvalidOperationException("DISK_LIMIT: less than 1 GiB free. Export source and explicitly clean inactive storage.");
        }
        void CheckRetainedStorage(CancellationToken token)
        {
            long size = 0;
            var directories = new Stack<string>(); directories.Push(root);
            if (root != registryRoot) directories.Push(registryRoot);
            while (directories.Count > 0)
            {
                string directory = directories.Pop(); ManagedSessionFiles.RejectLink(directory);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); ManagedSessionFiles.RejectLink(entry);
                    if (Directory.Exists(entry)) directories.Push(entry);
                    else size += new FileInfo(entry).Length;
                    if (size > ManagedSessionFiles.MaximumRetainedBytes) throw new InvalidOperationException("DISK_LIMIT: retained storage exceeds 64 GiB. Private source is preserved; explicitly export and clean inactive storage.");
                }
            }
        }
        static JObject ReadBoundedJson(string path)
        {
            if (!ManagedSessionFiles.TryReadMailboxText(path, 1048576, out string text, out string detail))
                throw new IOException("Mailbox metadata is unavailable: " + detail);
            return JObject.Parse(text);
        }
        static bool TryReadBoundedJson(string path, out JObject value)
        {
            value = null;
            if (!ManagedSessionFiles.TryReadMailboxText(path, 1048576, out string text, out _)) return false;
            try { value = JObject.Parse(text); return true; }
            catch (JsonException) { return false; }
        }
        void AddEvent(ManagedSessionRecord session, string type, string reason, string runId = null, string jobId = null)
        {
            session.events.Add(new ManagedEvent { sequence = session.nextSequence++, time = Now, type = type, reason = reason, runId = runId, jobId = jobId });
            while (session.events.Count > 128) session.events.RemoveAt(0);
        }
        void Save()
        {
            persistencePending = true;
            ManagedSessionFiles.WriteAtomic(statePath, JsonConvert.SerializeObject(state, Formatting.Indented));
            persistencePending = false;
        }
        static JObject SessionView(ManagedSessionRecord session)
        {
            var view = new JObject { ["sessionId"] = session.id, ["label"] = session.label, ["state"] = session.state,
                ["workspacePath"] = session.workspace, ["createdUnixMs"] = session.created, ["leaseUntilUnixMs"] = session.leaseUntil, ["sourceRevision"] = session.baselineRevision,
                ["notificationDelivery"] = "Poll unity_session action=events with afterSequence; client wake-up is not guaranteed.", ["failureDetail"] = session.error };
            string report = Path.Combine(session.workspace, "managed-snapshot-report.json");
            if (File.Exists(report))
            {
                if (TryReadBoundedJson(report, out JObject snapshotReport)) view["snapshotReport"] = snapshotReport;
                else view["snapshotReportStatus"] = "temporarily_unavailable";
            }
            return view;
        }
        static JObject BuildView(ManagedBuildRecord build) => new JObject { ["jobId"] = build.id, ["state"] = build.state, ["sourceRevision"] = build.revision,
            ["artifactPath"] = build.state == "succeeded" ? build.output : null, ["failureDetail"] = build.error, ["workerPid"] = build.pid, ["logPath"] = Path.Combine(Path.GetDirectoryName(build.project), "worker.log") };
        JObject RunView(ManagedRunRecord run)
        {
            var result = new JObject { ["runId"] = run.id, ["sessionId"] = run.sessionId, ["jobId"] = run.jobId, ["state"] = run.state, ["pid"] = run.pid,
                ["createdUnixMs"] = run.created, ["elapsedSeconds"] = (Now - run.created) / 1000, ["width"] = run.width, ["height"] = run.height, ["reason"] = run.reason };
            string readyPath = Path.Combine(run.mailbox, "ready.json");
            if (File.Exists(readyPath))
            {
                if (TryReadBoundedJson(readyPath, out JObject ready)) result["capabilities"] = ready["capabilities"];
                else result["capabilitiesStatus"] = "temporarily_unavailable";
            }
            string heartbeat = Path.Combine(run.mailbox, "player-heartbeat");
            if (ManagedSessionFiles.TryReadMailboxText(heartbeat, 64, out string heartbeatText, out string heartbeatDetail) && long.TryParse(heartbeatText, out long timestamp) && timestamp > 0)
            {
                result["heartbeatAgeMs"] = Math.Max(0, Now - timestamp);
                result["heartbeatStatus"] = "available";
            }
            else
            {
                result["heartbeatStatus"] = "temporarily_unavailable";
                result["heartbeatDetail"] = heartbeatDetail ?? "invalid_timestamp";
            }
            return result;
        }
    }
}
