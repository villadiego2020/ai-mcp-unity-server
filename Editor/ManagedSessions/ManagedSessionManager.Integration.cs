using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace AIUnityMCPServer
{
    [Serializable] internal sealed class ManagedIntegrationRecord
    {
        public string id, sessionId, requestId, state, baselineHash, planHash, errorCode, error;
        public long created;
    }

    internal sealed partial class ManagedSessionManager
    {
        const int MaximumIntegrationPlansPerSession = 4;
        readonly Dictionary<string, Task<string>> integrationTasks = new Dictionary<string, Task<string>>();

        // These locations are derived by the host, never accepted from a tool request.
        // Versioned baseline documents remain immutable when a later increment advances a pointer.
        internal string IntegrationDirectory(ManagedSessionRecord session) => Path.Combine(root, "i-" + session.id.Substring(8, 16));
        string IntegrationPlanDirectory(ManagedSessionRecord session, ManagedIntegrationRecord plan) => Path.Combine(IntegrationDirectory(session), "p-" + plan.id.Substring(5, 16));

        static void RequirePreviewAuthority(ManagedSessionRecord session)
        {
            // A retained, closed workspace may be reviewed while its original owner lease is
            // still valid. Closure never renews or restores an expired remote capability.
            if (session.leaseUntil <= Now || session.state == "expired") throw new UnauthorizedAccessException("OWNER_DENIED: the session owner lease has expired.");
            if (session.state != "ready" && session.state != "closed") throw new ManagedIntegrationException("WORKSPACE_NOT_READY", "Wait for a completed private snapshot before integration preview.");
        }

        JObject IntegrationCommand(ManagedSessionRecord session, string action, JObject request, bool trustedLocal = false)
        {
            if (!trustedLocal) RequirePreviewAuthority(session);
            if (action == "integration_apply" || action == "refresh_from_main") return AcceptMainOperation(session, action, request, trustedLocal);
            if (action == "integration_cancel") return CancelMainOperation(session, request);
            if (action == "integration_status")
            {
                string operationId = request.Value<string>("operationId");
                if (!string.IsNullOrEmpty(operationId)) return MainOperationStatus(session, operationId);
                string id = Required(request, "planId", 64);
                var record = state.integrations.FirstOrDefault(item => item.id == id && item.sessionId == session.id);
                if (record == null) throw new UnauthorizedAccessException("OWNER_DENIED: unknown integration preview.");
                CompleteIntegrationPlans();
                return IntegrationView(session, record, request.Value<int?>("offset") ?? 0, request.Value<int?>("limit") ?? 100);
            }
            // Reject legacy sessions explicitly, even if a similarly named sidecar happens to exist.
            return Mutate(session, "integration_plan", request, () =>
            {
                string baseline = ManagedSessionIntegration.BaselinePath(IntegrationDirectory(session), session.integrationBaselineHash);
                if (!File.Exists(baseline)) throw new ManagedIntegrationException("BASELINE_UNAVAILABLE", "The captured integration baseline is unavailable; existing source is preserved.");
                if (state.integrations.Count(item => item.sessionId == session.id && item.baselineHash == session.integrationBaselineHash) >= MaximumIntegrationPlansPerSession || state.integrations.Count(item => item.sessionId == session.id) >= 32)
                    throw new ManagedIntegrationException("PLAN_LIMIT", "Four previews per baseline and 32 immutable previews per session are retained. Published baselines release preview slots; source is preserved.");
                if (integrationTasks.Count != 0) throw new ManagedIntegrationException("INTEGRATION_BUSY", "One integration preview is already being prepared; poll it before submitting another.");
                CheckDiskBudget();
                var record = new ManagedIntegrationRecord
                {
                    id = "plan-" + Guid.NewGuid().ToString("N"), sessionId = session.id, requestId = request.Value<string>("requestId"),
                    state = "planning", baselineHash = session.integrationBaselineHash, created = Now
                };
                state.integrations.Add(record);
                var source = new CancellationTokenSource();
                source.CancelAfter(trustedLocal ? 1200000 : (int)Math.Min(int.MaxValue, Math.Max(1, session.leaseUntil - Now)));
                cancellation.Add(record.id, source);
                integrationTasks.Add(record.id, Task.Run(() =>
                {
                    CheckRetainedStorage(source.Token);
                    return ManagedSessionIntegration.CreatePlan(mainProject, session.workspace, IntegrationDirectory(session), record.baselineHash,
                        IntegrationPlanDirectory(session, record), record.id, session.id, source.Token);
                }, source.Token));
                return IntegrationView(session, record, 0, 100);
            });
        }

        void RecoverIntegrationPlans()
        {
            foreach (var record in state.integrations.Where(item => item.state == "planning"))
            {
                record.state = "failed"; record.errorCode = "PREVIEW_INTERRUPTED";
                record.error = "Integration preview was interrupted by Editor reload. Source is preserved; use a new request ID.";
            }
        }

        void CompleteIntegrationPlans()
        {
            bool changed = false;
            foreach (string id in integrationTasks.Keys.ToArray())
            {
                Task<string> task = integrationTasks[id];
                if (!task.IsCompleted) continue;
                integrationTasks.Remove(id); cancellation[id].Dispose(); cancellation.Remove(id);
                var record = state.integrations.First(item => item.id == id);
                Exception error = task.Exception?.GetBaseException();
                record.state = !task.IsCanceled && error == null ? "ready" : "failed";
                record.planHash = record.state == "ready" ? task.Result : null;
                record.errorCode = task.IsCanceled ? "PREVIEW_CANCELLED" : (error as ManagedIntegrationException)?.Code ?? (error == null ? null : "INTEGRATION_FAILED");
                record.error = task.IsCanceled ? "Preview cancelled or owner lease expired; source was preserved." : error?.Message;
                changed = true;
            }
            if (changed) Save();
        }

        void CancelIntegrationPlans()
        {
            foreach (string id in integrationTasks.Keys)
                if (cancellation.TryGetValue(id, out var source)) source.Cancel();
        }

        JObject IntegrationView(ManagedSessionRecord session, ManagedIntegrationRecord record, int offset, int limit)
        {
            if (offset < 0 || limit < 1 || limit > 500) throw new ArgumentException("Integration pagination requires offset >= 0 and limit from 1 to 500.");
            var response = new JObject
            {
                ["sessionId"] = session.id, ["planId"] = record.id, ["state"] = record.state, ["createdUnixMs"] = record.created,
                ["baselineHash"] = record.baselineHash, ["planHash"] = record.planHash, ["canApply"] = false, ["applyImplemented"] = true,
                ["scope"] = "Assets only. Preview does not write Main; publication requires an explicit reviewed plan hash, valid authority and Write Commands ON."
            };
            // NativeCommandTool treats the presence of a root error property as failure,
            // including a JSON null. Successful previews must omit failure fields entirely.
            if (!string.IsNullOrEmpty(record.error)) response["error"] = record.error;
            if (!string.IsNullOrEmpty(record.errorCode)) response["errorCode"] = record.errorCode;
            if (record.state != "ready") return response;
            var plan = ManagedSessionIntegration.ReadPlan(IntegrationPlanDirectory(session, record), record.planHash);
            if (plan.id != record.id || plan.sessionId != session.id || plan.baselineHash != record.baselineHash)
                throw new ManagedIntegrationException("PLAN_INTEGRITY", "Integration preview identity does not match its host record.");
            response["totalEntries"] = plan.entries.Count;
            response["offset"] = offset;
            long nextOffset = (long)offset + limit;
            response["nextOffset"] = nextOffset < plan.entries.Count ? (JToken)new JValue(nextOffset) : JValue.CreateNull();
            response["entries"] = JArray.FromObject(plan.entries.Skip(offset).Take(limit));
            response["counts"] = new JObject
            {
                ["added"] = plan.entries.Count(item => item.operation == "added"), ["modified"] = plan.entries.Count(item => item.operation == "modified"),
                ["deleted"] = plan.entries.Count(item => item.operation == "deleted"), ["conflicts"] = plan.entries.Count(item => item.status == "conflict"),
                ["alreadyApplied"] = plan.entries.Count(item => item.status == "already_applied"), ["unsupported"] = plan.entries.Count(item => item.status == "unsupported"),
                ["mainOnly"] = plan.mainOnlyChanges
            };
            response["canApply"] = record.baselineHash == session.integrationBaselineHash && plan.entries.Count <= ManagedSessionIntegration.MaximumApplyEntries &&
                plan.entries.Sum(item => item.bytes) <= ManagedSessionIntegration.MaximumApplyBytes && !plan.entries.Any(item => item.status == "conflict" || item.status == "unsupported");
            response["mainSnapshotHash"] = plan.mainSnapshotHash; response["privateSnapshotHash"] = plan.privateSnapshotHash;
            response["requiresRevalidation"] = true;
            response["snapshotNotice"] = "Immutable preview at creation. Status does not recheck current Main/private files; later edits require a new preview. Any future apply must revalidate the captured expectations.";
            return response;
        }
    }
}
