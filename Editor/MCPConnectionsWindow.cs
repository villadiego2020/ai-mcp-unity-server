using System;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer
{
    public sealed class MCPNativeStatus
    {
        public bool Available;
        public bool Running;
        public int RegisteredTools;
        public int EnabledTools;
        public int ConnectedClients;
        public string Details = "Native integration unavailable. Requires Assistant 2.18.0-pre.2 through 2.18.x; check the package version and compilation. Node/TCP and Unity CLI remain available.";
    }

    /// <summary>The optional native assembly owns these hooks for one Editor domain lifetime.</summary>
    public static class MCPNativeConnection
    {
        public static Func<MCPNativeStatus> ReadStatus;
        public static Action StartReadOnly;
        public static Action OpenSettings;
        public static MCPNativeStatus Status => ReadStatus?.Invoke() ?? new MCPNativeStatus();
    }

    public sealed class MCPConnectionsWindow : EditorWindow
    {
        Vector2 scrollPosition;
        [MenuItem("AI Unity MCP Server/Connections", priority = 1)]
        public static void Open()
        {
            var window = GetWindow<MCPConnectionsWindow>("MCP Connections");
            window.minSize = new Vector2(400, 430);
        }

        void OnInspectorUpdate() => Repaint();

        void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            var native = MCPNativeConnection.Status;
            EditorGUILayout.LabelField("AI Unity MCP Server", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Project", MCPPackagePaths.ProjectRoot());
            EditorGUILayout.LabelField("Editor instance", MCPServer.InstanceId);
            EditorGUILayout.LabelField("Package", MCPPackagePaths.PackageVersion());
            EditorGUILayout.Space();
            bool writes = MCPHandlers.AllowWrites;
            bool selected = EditorGUILayout.Toggle("Allow Write Commands", writes);
            if (selected != writes) MCPHandlers.AllowWrites = selected;
            EditorGUILayout.HelpBox("The write switch protects this package's tools on every connection. Unity's built-in tools have separate permissions in Native Settings.", MessageType.Info);

            EditorGUILayout.LabelField("Native Unity MCP (compatibility)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("Server", native.Available ? native.Running ? "Running" : "Stopped" : "Unavailable");
            EditorGUILayout.LabelField("Our tools", $"{native.EnabledTools} enabled / {native.RegisteredTools} registered");
            EditorGUILayout.LabelField("Connected clients", native.ConnectedClients.ToString());
            EditorGUILayout.HelpBox(native.Details, native.Available ? MessageType.Info : MessageType.Warning);
            using (new EditorGUI.DisabledScope(!native.Available))
            {
                if (GUILayout.Button("Start Native with Our Writes OFF")) MCPNativeConnection.StartReadOnly?.Invoke();
                if (GUILayout.Button("Native Settings / Configure Client")) MCPNativeConnection.OpenSettings?.Invoke();
            }
            EditorGUILayout.HelpBox("Enable the AI Unity MCP Server tool group in Native Settings if previously disabled. Native uses Unity's existing relay; it starts no extra Node process. Unity is migrating Native MCP to Unity CLI.", MessageType.Info);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Node/TCP and Unity CLI", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("TCP", MCPServer.IsRunning ? $"Running on port {MCPServer.Port}" : "Stopped");
            if (GUILayout.Button("Start TCP with Writes OFF"))
            {
                MCPHandlers.AllowWrites = false;
                MCPServer.Start();
            }
            if (GUILayout.Button("Configure Node Client")) EditorApplication.ExecuteMenuItem("AI Unity MCP Server/Setup/Configure Codex");
            if (GUILayout.Button("Connection Doctor")) EditorApplication.ExecuteMenuItem("AI Unity MCP Server/Setup/Doctor");
            EditorGUILayout.HelpBox("Unity CLI exposes ai_mcp_list_commands (full tool schemas) and ai_mcp_dispatch. Target this exact project. Activity records all calls to our dispatcher, including Native and Pipeline.", MessageType.None);
            EditorGUILayout.EndScrollView();
        }
    }
}
