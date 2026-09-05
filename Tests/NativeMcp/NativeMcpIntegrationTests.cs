#if AI_UNITY_MCP_NATIVE_ASSISTANT
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AIUnityMCPServer.NativeMcp;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.AI.MCP.Editor.ToolRegistry;
using UnityEngine;
using UnityEngine.TestTools;

namespace AIUnityMCPServer.Tests
{
    public class NativeMcpIntegrationTests
    {
        static MCPCommandDefinition Definition(string name) => MCPCommandCatalog.Load().Single(item => item.ToolName == name);

        [SetUp]
        public void SetUp()
        {
            ClearRateLimit();
            NativeMcpIntegration.RegisterTools();
        }

        [TearDown]
        public void TearDown()
        {
            ClearRateLimit();
            NativeMcpIntegration.RegisterTools();
        }

        [Test]
        public void RegistrationAndRepeatedRefreshPublishEveryManifestToolExactlyOnce()
        {
            for (int iteration = 0; iteration < 3; iteration++)
            {
                if (iteration > 0) McpToolRegistry.RefreshTools();
                NativeMcpIntegration.RegisterTools();
                var tools = McpToolRegistry.GetAllToolsForSettings().Select(entry => entry.Info).ToArray();
                foreach (var definition in MCPCommandCatalog.Load())
                {
                    var matching = tools.Where(tool => tool.name == definition.ToolName).ToArray();
                    Assert.That(matching.Length, Is.EqualTo(1), definition.ToolName);
                    Assert.That(JToken.DeepEquals(JToken.FromObject(matching[0].inputSchema), definition.GetInputSchema()), Is.True, definition.ToolName);
                }
                Assert.That(NativeMcpIntegration.GetStatus().RegisteredTools, Is.EqualTo(73));
            }
        }

        [UnityTest]
        public IEnumerator CollisionDoesNotOverwriteOrRemoveAnotherOwnersTool()
        {
            NativeMcpIntegration.Shutdown();
            McpToolRegistry.RegisterTool("unity_ping", new SentinelTool(), "Isolated ownership sentinel", true);
            try
            {
                NativeMcpIntegration.RegisterTools();
                Assert.That(NativeMcpIntegration.GetStatus().RegisteredTools, Is.EqualTo(72));
                Assert.That(NativeMcpIntegration.GetStatus().Details, Does.Contain("unity_ping"));
                var execution = McpToolRegistry.ExecuteToolAsync("unity_ping", new JObject());
                while (!execution.IsCompleted) yield return null;
                Assert.That(((JObject)execution.GetAwaiter().GetResult()).Value<string>("owner"), Is.EqualTo("sentinel"));
                NativeMcpIntegration.Shutdown();
                Assert.That(McpToolRegistry.HasTool("unity_ping"), Is.True);
            }
            finally
            {
                McpToolRegistry.UnregisterTool("unity_ping");
                NativeMcpIntegration.RegisterTools();
            }
        }

        [Test]
        public void ExplicitNativeToolDisableSurvivesRegistrationAndRefresh()
        {
            Type manager = typeof(McpToolRegistry).Assembly.GetType("Unity.AI.MCP.Editor.Settings.MCPSettingsManager", true);
            object settings = manager.GetProperty("Settings", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            MethodInfo enabled = settings.GetType().GetMethod("IsToolEnabled");
            MethodInfo set = settings.GetType().GetMethod("SetToolEnabled");
            bool previous = (bool)enabled.Invoke(settings, new object[] { "unity_ping" });
            try
            {
                set.Invoke(settings, new object[] { "unity_ping", false });
                NativeMcpIntegration.RegisterTools();
                McpToolRegistry.RefreshTools();
                Assert.That((bool)enabled.Invoke(settings, new object[] { "unity_ping" }), Is.False);
                Assert.That(McpToolRegistry.GetAvailableTools().Any(tool => tool.name == "unity_ping"), Is.False);
            }
            finally { set.Invoke(settings, new object[] { "unity_ping", previous }); }
        }

        [Test]
        public void LaterThirdPartyReplacementSurvivesAdapterShutdown()
        {
            McpToolRegistry.RegisterTool("unity_ping", new SentinelTool(), "Replacement ownership sentinel", true);
            try
            {
                NativeMcpIntegration.RegisterTools();
                NativeMcpIntegration.Shutdown();
                Assert.That(McpToolRegistry.HasTool("unity_ping"), Is.True);
                var result = (JObject)McpToolRegistry.ExecuteToolAsync("unity_ping", new JObject()).GetAwaiter().GetResult();
                Assert.That(result.Value<string>("owner"), Is.EqualTo("sentinel"));
            }
            finally
            {
                McpToolRegistry.UnregisterTool("unity_ping");
                NativeMcpIntegration.RegisterTools();
            }
        }

        [UnityTest]
        public IEnumerator NativePingAndSourceInspectionRunWithTcpStoppedAndRecordSource()
        {
            // Batch-mode test hosts suppress TCP startup; avoid changing the user's global auto-start preference.
            Assert.That(MCPServer.IsRunning, Is.False, "Use an isolated batch-mode host for this test.");
            string relative = "Assets/__NativeMcp_" + Guid.NewGuid().ToString("N") + ".uxml";
            string absolute = Path.Combine(Application.dataPath, Path.GetFileName(relative));
            File.WriteAllText(absolute, "<UXML><Button name=\"sentinel-button\" text=\"Test\"/></UXML>");
            try
            {
                var ping = McpToolRegistry.ExecuteToolAsync("unity_ping", new JObject());
                while (!ping.IsCompleted) yield return null;
                var pong = JObject.FromObject(ping.GetAwaiter().GetResult());
                Assert.That(pong["data"].Value<string>("status"), Is.EqualTo("ok"));
                Assert.That(pong["structuredContent"]["data"].Value<string>("status"), Is.EqualTo("ok"));
                var inspect = McpToolRegistry.ExecuteToolAsync("unity_uitk_inspect", new JObject { ["path"] = relative });
                while (!inspect.IsCompleted) yield return null;
                var inspected = JObject.FromObject(inspect.GetAwaiter().GetResult());
                Assert.That(inspected["data"].Value<bool>("ok"), Is.True, inspected.ToString());
                Assert.That(inspected["data"].ToString(), Does.Contain("sentinel-button"));
                Assert.That(MCPHandlers.Log.Last().Source, Is.EqualTo("Native"));
                Assert.That(MCPHandlers.Log.Last().Path, Is.EqualTo("/uitk/inspect"));
                Assert.That(MCPServer.IsRunning, Is.False);
            }
            finally
            {
                if (File.Exists(absolute)) File.Delete(absolute);
                if (File.Exists(absolute + ".meta")) File.Delete(absolute + ".meta");
            }
        }

        [Test]
        public void InvalidToolInputsAndHandlerErrorsAreFailuresInsteadOfSuccessEnvelopes()
        {
            var tool = new NativeCommandTool(Definition("unity_uitk_inspect"));
            Assert.Throws<ArgumentException>(() => tool.ExecuteAsync(new JArray()));
            Assert.Throws<ArgumentException>(() => tool.ExecuteAsync(new JObject()));
            Assert.Throws<ArgumentException>(() => tool.ExecuteAsync(new JObject { ["path"] = "Assets/Test.uxml", ["maxNodes"] = 1.5 }));
            Assert.Throws<InvalidOperationException>(() => tool.ExecuteAsync(new JObject { ["path"] = "../escape.uxml" }));
        }

        [Test]
        public void NativeWriteGateBlocksMutationAndAllowsExplicitSessionOptIn()
        {
            bool previous = MCPHandlers.AllowWrites;
            var fixture = new GameObject("NativeMcp_WriteSentinel_" + Guid.NewGuid().ToString("N"));
            var tool = new NativeCommandTool(Definition("unity_set_transform"));
            var args = new JObject { ["name"] = fixture.name, ["set"] = "pos", ["px"] = 3, ["py"] = 4, ["pz"] = 5 };
            try
            {
                MCPHandlers.AllowWrites = false;
                Assert.Throws<InvalidOperationException>(() => tool.ExecuteAsync(args));
                Assert.That(fixture.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(MCPHandlers.Log.Last().IsError, Is.True);
                MCPHandlers.AllowWrites = true;
                var success = (JObject)tool.ExecuteAsync(args).GetAwaiter().GetResult();
                Assert.That(success.Value<bool>("success"), Is.True);
                Assert.That(fixture.transform.position, Is.EqualTo(new Vector3(3, 4, 5)));
                Assert.That(MCPHandlers.Log.Last().Source, Is.EqualTo("Native"));
            }
            finally
            {
                MCPHandlers.AllowWrites = previous;
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        static void ClearRateLimit()
        {
            object queue = typeof(MCPHandlers).GetField("_recent", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            queue.GetType().GetMethod("Clear").Invoke(queue, null);
        }

        sealed class SentinelTool : IUnityMcpTool
        {
            public Task<object> ExecuteAsync(object parameters) => Task.FromResult<object>(new JObject { ["owner"] = "sentinel" });
            public object GetInputSchema() => new JObject { ["type"] = "object" };
        }
    }
}
#endif
