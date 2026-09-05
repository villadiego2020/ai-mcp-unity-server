#if AI_UNITY_MCP_NATIVE_ASSISTANT
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.AI.MCP.Editor;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEditor;
using UnityEngine;

namespace AIUnityMCPServer.NativeMcp
{
    [InitializeOnLoad]
    public static class NativeMcpIntegration
    {
        const string ToolGroup = "AI Unity MCP Server";
        static readonly Dictionary<string, NativeCommandTool> ownedTools = new Dictionary<string, NativeCommandTool>(StringComparer.Ordinal);
        static readonly List<string> collisions = new List<string>();
        static string registrationError = "";
        static bool subscribed;
        static MCPNativeStatus cachedStatus;
        static double nextStatusRefresh;
        const double StatusRefreshSeconds = 1;

        static NativeMcpIntegration()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            Subscribe();
            EditorApplication.delayCall += RegisterTools;
        }

        static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            McpToolRegistry.ToolsChanged += OnToolsChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            EditorApplication.quitting += Shutdown;
            MCPNativeConnection.ReadStatus = GetStatus;
            MCPNativeConnection.StartReadOnly = StartReadOnly;
            MCPNativeConnection.OpenSettings = OpenSettings;
        }

        public static void RegisterTools()
        {
            if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            Subscribe();
            collisions.Clear();
            registrationError = "";
            try
            {
                foreach (var definition in MCPCommandCatalog.Load()) RegisterTool(definition);
            }
            catch (Exception exception)
            {
                registrationError = exception.Message;
                Debug.LogError($"[AI Unity MCP Server] Native tool registration failed: {exception.Message}");
            }
        }

        static void RegisterTool(MCPCommandDefinition definition)
        {
            string name = McpToolRegistry.SanitizeToolName(definition.ToolName);
            if (McpToolRegistry.HasTool(name))
            {
                if (!OwnsCurrentTool(name)) collisions.Add(name);
                return;
            }
            var tool = new NativeCommandTool(definition);
            McpToolRegistry.RegisterTool(name, tool, definition.Description, enabledByDefault: true, groups: new[] { ToolGroup });
            ownedTools[name] = tool;
        }

        static bool OwnsCurrentTool(string name)
        {
            if (!ownedTools.TryGetValue(name, out var owner)) return false;
            // Public registry metadata preserves the schema object supplied by the instance.
            // Check identity before cleanup so a later third-party replacement is never removed.
            return McpToolRegistry.GetAllToolsForSettings().Any(entry => entry.Info.name == name &&
                ReferenceEquals(entry.Info.inputSchema, owner.GetInputSchema()));
        }

        static void OnToolsChanged(McpToolRegistry.ToolChangeEventArgs change)
        {
            cachedStatus = null;
            if (change.ChangeType != McpToolRegistry.ToolChangeType.Refreshed) return;
            ownedTools.Clear();
            RegisterTools();
        }

        public static MCPNativeStatus GetStatus()
        {
            double now = EditorApplication.timeSinceStartup;
            if (cachedStatus != null && now < nextStatusRefresh) return cachedStatus;
            var current = McpToolRegistry.GetAllToolsForSettings()
                .Where(entry => ownedTools.TryGetValue(entry.Info.name, out var owner) &&
                    ReferenceEquals(entry.Info.inputSchema, owner.GetInputSchema())).ToArray();
            var available = new HashSet<string>(McpToolRegistry.GetAvailableTools().Select(tool => tool.name));
            string details = "Shared tools register automatically. Existing Native tool and group choices are preserved.";
            if (collisions.Count > 0) details += " Names owned by other tools were skipped: " + string.Join(", ", collisions);
            if (!string.IsNullOrEmpty(registrationError)) details += " Registration error: " + registrationError;
            cachedStatus = new MCPNativeStatus
            {
                Available = true,
                Running = UnityMCPBridge.IsRunning,
                RegisteredTools = current.Length,
                EnabledTools = current.Count(entry => entry.IsEnabled && available.Contains(entry.Info.name)),
                ConnectedClients = UnityMCPBridge.GetConnectedClientCount(),
                Details = details
            };
            nextStatusRefresh = now + StatusRefreshSeconds;
            return cachedStatus;
        }

        static void StartReadOnly()
        {
            MCPHandlers.AllowWrites = false;
            UnityMCPBridge.Enabled = true;
            UnityMCPBridge.Start();
            cachedStatus = null;
        }

        static void OpenSettings() => SettingsService.OpenProjectSettings("Project/AI/Unity MCP Server");

        public static void Shutdown()
        {
            EditorApplication.delayCall -= RegisterTools;
            McpToolRegistry.ToolsChanged -= OnToolsChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
            EditorApplication.quitting -= Shutdown;
            foreach (string name in ownedTools.Keys.ToArray())
                if (OwnsCurrentTool(name)) McpToolRegistry.UnregisterTool(name);
            ownedTools.Clear();
            cachedStatus = null;
            MCPNativeConnection.ReadStatus = null;
            MCPNativeConnection.StartReadOnly = null;
            MCPNativeConnection.OpenSettings = null;
            subscribed = false;
        }
    }
}
#endif
