using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    public sealed class ManagedSessionsWindow : EditorWindow
    {
        Vector2 scroll;
        sealed class ReviewPanel { internal JObject preview; internal string planId, operationId, failure; internal int offset; }
        readonly Dictionary<string, ReviewPanel> reviews = new Dictionary<string, ReviewPanel>();
        [MenuItem("AI Unity MCP Server/Managed Sessions", priority = 2)]
        public static void Open()
        {
            var window = GetWindow<ManagedSessionsWindow>("Managed Sessions");
            window.minSize = new Vector2(550, 360);
        }
        void OnInspectorUpdate() => Repaint();
        void OnGUI()
        {
            ManagedSessionHost.Initialize();
            var manager = ManagedSessionHost.Manager;
            EditorGUILayout.LabelField("AI Unity MCP Server — Managed Sessions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Each session owns a private saved-source project and standalone Player. One headless Unity worker builds at a time. Closing preserves private source; nothing merges into the main project automatically.", MessageType.Info);
            string activation = ManagedSessionQualification.ActivationReason;
            if (activation != null) { EditorGUILayout.HelpBox(activation, MessageType.Warning); return; }
            string exclusion = ManagedSessionQualification.ExclusionReason(Application.unityVersion);
            if (exclusion != null) { EditorGUILayout.HelpBox(exclusion, MessageType.Warning); return; }
            if (manager == null) { EditorGUILayout.LabelField("Managed host unavailable."); return; }
            int active = manager.Runs.Count(item => item.state == "starting" || item.state == "running" || item.state == "closing");
            EditorGUILayout.LabelField("Active Player windows", active + " / 2");
            EditorGUILayout.LabelField("Active build workers", manager.Builds.Count(item => item.state == "building") + " / 1");
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var session in manager.Sessions)
            {
                EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                EditorGUILayout.LabelField(session.label + " — " + session.state, EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(session.id, GUILayout.Height(18));
                EditorGUILayout.LabelField("Elapsed", TimeSpan.FromMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - session.created).ToString(@"hh\:mm\:ss"));
                EditorGUILayout.SelectableLabel(session.workspace, GUILayout.Height(18));
                if (!string.IsNullOrEmpty(session.error)) EditorGUILayout.HelpBox(session.error, MessageType.Warning);
                foreach (var build in manager.Builds.Where(item => item.sessionId == session.id))
                    EditorGUILayout.LabelField("Build " + build.id.Substring(6, 8), build.state + (string.IsNullOrEmpty(build.revision) ? "" : " / " + build.revision.Substring(0, 12)));
                foreach (var run in manager.Runs.Where(item => item.sessionId == session.id))
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(run.id.Substring(0, 16), run.state + " · PID " + run.pid + " · " + (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - run.created) / 1000 + "s");
                    using (new EditorGUI.DisabledScope(run.state == "closed" || run.state == "closing"))
                        if (GUILayout.Button("Close Player", GUILayout.Width(110))) manager.CloseRun(run, "user_closed");
                    EditorGUILayout.EndHorizontal();
                }
                if (session.state != "closed" && session.state != "expired")
                    if (GUILayout.Button("Close Session (preserve private source)")) manager.CloseSession(session, "user_closed");
                DrawIntegration(manager, session);
                EditorGUILayout.EndVertical();
            }
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button("Show Managed Storage")) EditorUtility.RevealInFinder(manager.Root);
        }

        void DrawIntegration(ManagedSessionManager manager, ManagedSessionRecord session)
        {
            if (!reviews.TryGetValue(session.id, out var panel)) reviews.Add(session.id, panel = new ReviewPanel());
            if (string.IsNullOrEmpty(panel.operationId)) panel.operationId = manager.MainOperations.LastOrDefault(item => item.sessionId == session.id)?.id;
            bool pending = manager.MainOperations.Any(item => item.sessionId == session.id && !new[] { "succeeded", "compile_failed", "failed", "cancelled", "rolled_back", "recovery_required", "validation_interrupted" }.Contains(item.state));
            try
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Explicit source integration", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(session.state == "queued" || session.state == "snapshotting"))
                    if (GUILayout.Button("Review private changes against Main"))
                    {
                        panel.preview = manager.BeginLocalReview(session.id); panel.planId = panel.preview.Value<string>("planId"); panel.failure = null; panel.offset = 0;
                    }
                if (!string.IsNullOrEmpty(panel.planId) && panel.preview?.Value<string>("state") == "planning")
                    panel.preview = manager.ReadLocalIntegration(session.id, panel.planId);
                if (panel.preview != null)
                {
                    EditorGUILayout.LabelField("Preview", panel.preview.Value<string>("state") + " / " + panel.planId);
                    if (panel.preview["counts"] != null) EditorGUILayout.LabelField("Full plan counts", panel.preview["counts"].ToString(Newtonsoft.Json.Formatting.None));
                    foreach (var entry in (panel.preview["entries"] as JArray ?? new JArray()))
                        EditorGUILayout.LabelField(entry.Value<string>("path"), entry.Value<string>("operation") + " / " + entry.Value<string>("status"));
                    EditorGUILayout.BeginHorizontal();
                    using (new EditorGUI.DisabledScope(panel.offset == 0)) if (GUILayout.Button("Previous 100 changes")) { panel.offset = Math.Max(0, panel.offset - 100); panel.preview = manager.ReadLocalIntegration(session.id, panel.planId, offset: panel.offset); }
                    using (new EditorGUI.DisabledScope(panel.preview["nextOffset"]?.Type != JTokenType.Integer)) if (GUILayout.Button("Next 100 changes")) { panel.offset += 100; panel.preview = manager.ReadLocalIntegration(session.id, panel.planId, offset: panel.offset); }
                    EditorGUILayout.EndHorizontal();
                    bool eligible = panel.preview.Value<bool?>("canApply") == true && panel.preview.Value<string>("baselineHash") == session.integrationBaselineHash;
                    using (new EditorGUI.DisabledScope(!eligible || !MCPHandlers.AllowWrites || pending))
                        if (GUILayout.Button("Apply this reviewed Assets plan to Main")) panel.operationId = manager.ApplyLocalReview(session.id, panel.planId, panel.preview.Value<string>("planHash")).Value<string>("operationId");
                }
                using (new EditorGUI.DisabledScope(!MCPHandlers.AllowWrites || pending || session.state == "queued" || session.state == "snapshotting"))
                    if (GUILayout.Button("Refresh pristine private source from saved Main (retain old source)")) panel.operationId = manager.RefreshLocalWorkspace(session.id).Value<string>("operationId");
                if (!string.IsNullOrEmpty(panel.operationId))
                {
                    var operation = manager.ReadLocalIntegration(session.id, null, panel.operationId);
                    EditorGUILayout.LabelField("Main operation", operation.Value<string>("state") + " / " + operation.Value<string>("compileResult"));
                    if (operation.Value<bool?>("sourceApplied") == true) EditorGUILayout.LabelField("Source", "Applied; other workspaces and Players remain pinned.");
                    string detail = operation.Value<string>("failureDetail"); if (!string.IsNullOrEmpty(detail)) EditorGUILayout.HelpBox(detail, MessageType.Warning);
                    using (new EditorGUI.DisabledScope(!pending || operation.Value<bool?>("sourceApplied") == true))
                        if (GUILayout.Button("Cancel this queued Main operation (preserve source)")) manager.CancelLocalOperation(session.id, panel.operationId);
                    foreach (var diagnostic in operation["diagnostics"] as JArray ?? new JArray()) EditorGUILayout.HelpBox(diagnostic.Value<string>("message"), MessageType.Warning);
                }
            }
            catch (Exception error) { panel.failure = error.Message; }
            if (!string.IsNullOrEmpty(panel.failure)) EditorGUILayout.HelpBox(panel.failure, MessageType.Warning);
        }
    }
}
