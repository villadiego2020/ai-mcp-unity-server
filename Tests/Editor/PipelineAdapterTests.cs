using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using Unity.Pipeline.Commands;
using UnityEngine;

namespace AIUnityMCPServer.Tests
{
    public class PipelineAdapterTests
    {
        bool originalAllowWrites;

        [SetUp]
        public void SetUp()
        {
            originalAllowWrites = MCPHandlers.AllowWrites;
            ClearRateLimitQueue();
        }

        [TearDown]
        public void TearDown()
        {
            MCPHandlers.AllowWrites = originalAllowWrites;
            ClearRateLimitQueue();
        }

        [Test]
        public void Adapter_RegistersExactlyTwoUniqueMainThreadCommands()
        {
            var commands = typeof(MCPHandlers)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Select(method => new
                {
                    Method = method,
                    Attribute = method.GetCustomAttribute<CliCommandAttribute>()
                })
                .Where(item => item.Attribute != null)
                .ToArray();

            CollectionAssert.AreEquivalent(
                new[] { "ai_mcp_list_commands", "ai_mcp_dispatch" },
                commands.Select(item => item.Attribute.Name));
            Assert.AreEqual(2, commands.Select(item => item.Attribute.Name).Distinct().Count());
            Assert.IsTrue(commands.All(item => item.Attribute.MainThreadRequired));
        }

        [Test]
        public void ListCommands_EqualsDispatcherRoutesAndCoversManifest()
        {
            var listed = (JObject)MCPHandlers.ListPipelineCommands();
            var listedPaths = listed["commands"].Values<string>().ToArray();
            var dispatcherPaths = MCPHandlers.CommandPaths().ToArray();
            string manifestPath = MCPPackagePaths.CommandManifestPath();
            var manifest = JObject.Parse(File.ReadAllText(manifestPath));
            var manifestPaths = manifest["commands"].Values<JObject>()
                .Select(command => command.Value<string>("path"))
                .ToArray();

            Assert.AreEqual(73, manifestPaths.Length);
            CollectionAssert.AreEqual(dispatcherPaths, listedPaths);
            CollectionAssert.IsSubsetOf(manifestPaths, listedPaths);
            Assert.AreEqual(MCPHandlers.AllowWrites, listed.Value<bool>("writeCommandsAllowed"));
        }

        [Test]
        public void Dispatch_AcceptsKnownAliasAndRoute()
        {
            var ping = (JObject)MCPHandlers.DispatchPipelineCommand("ping", "{}");
            var hierarchy = (JObject)MCPHandlers.DispatchPipelineCommand("/scene/hierarchy", "{}");

            Assert.AreEqual("ok", ping.Value<string>("status"));
            Assert.That(hierarchy["hierarchy"], Is.TypeOf<JArray>());
            Assert.That(MCPHandlers.Log.Last().Source, Is.EqualTo("Pipeline"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("[]")]
        [TestCase("not-json")]
        public void Dispatch_RejectsMalformedOrNonObjectBody(string body)
        {
            Assert.Throws<ArgumentException>(() =>
                MCPHandlers.DispatchPipelineCommand("ping", body));
        }

        [Test]
        public void Dispatch_ConvertsLegacyAndStructuredFailuresToExceptions()
        {
            var legacy = Assert.Throws<InvalidOperationException>(() =>
                MCPHandlers.DispatchPipelineCommand("/does-not-exist", "{}"));
            var structured = Assert.Throws<InvalidOperationException>(() =>
                MCPHandlers.DispatchPipelineCommand("uitk_inspect", "{}"));

            StringAssert.Contains("Unknown command: /does-not-exist", legacy.Message);
            StringAssert.StartsWith("INVALID_REQUEST:", structured.Message);
        }

        [Test]
        public void Dispatch_BoundsAndSanitizesStructuredErrorMessage()
        {
            MethodInfo method = typeof(MCPHandlers).GetMethod(
                "BuildPipelineErrorMessage",
                BindingFlags.NonPublic | BindingFlags.Static);
            var error = new JObject
            {
                ["code"] = "TEST",
                ["message"] = new string('x', 300) + "\r\n\t" + new string('y', 300)
            };

            string message = (string)method.Invoke(null, new object[] { error });

            Assert.LessOrEqual(message.Length, 518);
            Assert.IsFalse(message.Any(char.IsControl));
            StringAssert.StartsWith("TEST: ", message);
        }

        [Test]
        public void Dispatch_WriteGateBlocksMutationThenAllowsReversibleMutation()
        {
            var fixture = new GameObject("PipelineAdapter_WriteFixture");
            try
            {
                fixture.transform.localPosition = Vector3.zero;
                const string body = "{\"name\":\"PipelineAdapter_WriteFixture\",\"set\":\"pos\",\"px\":3,\"py\":4,\"pz\":5}";

                MCPHandlers.AllowWrites = false;
                Assert.Throws<InvalidOperationException>(() =>
                    MCPHandlers.DispatchPipelineCommand("set_transform", body));
                Assert.AreEqual(Vector3.zero, fixture.transform.localPosition);

                MCPHandlers.AllowWrites = true;
                var result = (JObject)MCPHandlers.DispatchPipelineCommand("/object/set-transform", body);
                Assert.AreEqual("PipelineAdapter_WriteFixture", result.Value<string>("transformed"));
                Assert.AreEqual(new Vector3(3f, 4f, 5f), fixture.transform.localPosition);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        [Test]
        public void Dispatch_DoesNotBypassDispatcherRateLimit()
        {
            for (int index = 0; index < 25; index++)
            {
                var result = (JObject)MCPHandlers.DispatchPipelineCommand("ping", "{}");
                Assert.AreEqual("ok", result.Value<string>("status"));
            }

            var exception = Assert.Throws<InvalidOperationException>(() =>
                MCPHandlers.DispatchPipelineCommand("ping", "{}"));
            StringAssert.Contains("Rate limit exceeded", exception.Message);
        }

        static void ClearRateLimitQueue()
        {
            FieldInfo field = typeof(MCPHandlers).GetField(
                "_recent",
                BindingFlags.NonPublic | BindingFlags.Static);
            var queue = (ICollection)field.GetValue(null);
            MethodInfo clear = queue.GetType().GetMethod("Clear");
            clear.Invoke(queue, null);
        }
    }
}
