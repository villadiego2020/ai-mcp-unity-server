using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace MCPBridge.Tests
{
    public class ConnectionSetupTests
    {
        [Serializable]
        sealed class CommandManifest
        {
            public CommandEntry[] commands;
        }

        [Serializable]
        sealed class CommandEntry
        {
            public string tool;
            public string path;
        }

        [Test]
        public void PackagePaths_ResolveServerEntryAndCommandManifestFromInstalledPackage()
        {
            string packageRoot = MCPPackagePaths.PackageRoot();
            string serverEntry = MCPPackagePaths.ServerEntryPath();
            string manifest = MCPPackagePaths.CommandManifestPath();

            Assert.That(packageRoot, Is.Not.Empty);
            Assert.That(File.Exists(Path.Combine(packageRoot, "package.json")), Is.True, packageRoot);
            Assert.That(File.Exists(serverEntry), Is.True, serverEntry);
            Assert.That(File.Exists(manifest), Is.True, manifest);
            Assert.That(Path.GetFileName(serverEntry), Is.EqualTo("index.js"));
            Assert.That(Path.GetFileName(manifest), Is.EqualTo("commands.json"));
            StringAssert.Contains(Path.Combine("AIUnityMCPServer", "runtime"), MCPPackagePaths.RuntimeCacheRoot());
        }

        [Test]
        public void CommandManifest_HasUniqueToolsAndRoutes()
        {
            string json = File.ReadAllText(MCPPackagePaths.CommandManifestPath());
            var manifest = JsonUtility.FromJson<CommandManifest>(json);
            Assert.That(manifest, Is.Not.Null);
            Assert.That(manifest.commands, Has.Length.EqualTo(69));

            var tools = new HashSet<string>(StringComparer.Ordinal);
            var routes = new HashSet<string>(StringComparer.Ordinal);
            foreach (CommandEntry command in manifest.commands)
            {
                Assert.That(tools.Add(command.tool), Is.True, $"Duplicate MCP tool: {command.tool}");
                Assert.That(routes.Add(command.path), Is.True, $"Duplicate Unity route: {command.path}");
            }
        }

        [Test]
        public void SetupPathComparison_NormalizesEquivalentPathsButRejectsDifferentServer()
        {
            string expected = Path.Combine(MCPPackagePaths.ServerDirectory(), "folder", "..", "index.js");
            string normalized = Path.Combine(MCPPackagePaths.ServerDirectory(), "index.js");
            string officialServer = Path.Combine(MCPPackagePaths.ServerDirectory(), "official-unity-mcp.js");

            Assert.That(InvokeSetup<bool>("PathsEqual", expected, normalized), Is.True);
            Assert.That(InvokeSetup<bool>("PathsEqual", normalized, officialServer), Is.False);
            Assert.That(InvokeSetup<bool>("PathsEqual", "", normalized), Is.False);
        }

        [Test]
        public void SetupArguments_QuotesServerPathWithoutChangingItsValue()
        {
            var arguments = new[] { "mcp", "add", "unity", "--", "node", "C:\\Path With Spaces\\Server~\\index.js" };
            string commandLine = InvokeSetup<string>("BuildArguments", (object)arguments);
            Assert.That(commandLine, Is.EqualTo("mcp add unity -- node \"C:\\Path With Spaces\\Server~\\index.js\""));
        }

        [Test]
        public void WriteGate_BlocksMutationWhenDisabled()
        {
            bool previous = MCPHandlers.AllowWrites;
            try
            {
                MCPHandlers.AllowWrites = false;
                string result = MCPHandlers.Dispatch("/gameobject/create", "{}", false);
                StringAssert.Contains("READ-ONLY", result);
            }
            finally
            {
                MCPHandlers.AllowWrites = previous;
            }
        }

        [Test]
        public void GeneratedConfig_DoesNotClaimOfficialUnityMcpConfiguration()
        {
            const string officialConfig = "{\n  \"mcpServers\": {\n    \"unityMCP\": { \"command\": \"unity\", \"args\": [\"mcp\"] }\n  }\n}\n";
            Assert.That(MCPServer.IsGeneratedByThisPackage(officialConfig), Is.False);
        }

        [Test]
        public void RuntimeBootstrap_IsAtomicAndIdempotent()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcp-runtime-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                MCPRuntimeCache.Plan plan = CreateRuntimePlan(root);
                Assert.That(MCPRuntimeCache.Inspect(plan, out _), Is.EqualTo(MCPRuntimeCache.CacheState.Missing));

                Assert.That(MCPRuntimeCache.TryEnsure(plan, out bool firstChanged, out string firstFailure), Is.True, firstFailure);
                Assert.That(firstChanged, Is.True);
                Assert.That(MCPRuntimeCache.Inspect(plan, out string readyDetail), Is.EqualTo(MCPRuntimeCache.CacheState.Ready), readyDetail);
                DateTime firstWrite = File.GetLastWriteTimeUtc(plan.ServerEntryPath);
                string firstContent = File.ReadAllText(plan.ServerEntryPath);

                Assert.That(MCPRuntimeCache.TryEnsure(plan, out bool secondChanged, out string secondFailure), Is.True, secondFailure);
                Assert.That(secondChanged, Is.False);
                Assert.That(File.GetLastWriteTimeUtc(plan.ServerEntryPath), Is.EqualTo(firstWrite));
                Assert.That(File.ReadAllText(plan.ServerEntryPath), Is.EqualTo(firstContent));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void RuntimeBootstrap_QuarantinesCorruptionBeforeRepair()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcp-runtime-repair-" + Guid.NewGuid().ToString("N"));
            try
            {
                MCPRuntimeCache.Plan plan = CreateRuntimePlan(root);
                Assert.That(MCPRuntimeCache.TryEnsure(plan, out _, out string setupFailure), Is.True, setupFailure);
                File.WriteAllText(plan.ServerEntryPath, "corrupted");
                Assert.That(MCPRuntimeCache.Inspect(plan, out _), Is.EqualTo(MCPRuntimeCache.CacheState.Corrupt));

                Assert.That(MCPRuntimeCache.TryEnsure(plan, out bool changed, out string repairFailure), Is.True, repairFailure);
                Assert.That(changed, Is.True);
                Assert.That(MCPRuntimeCache.Inspect(plan, out string readyDetail), Is.EqualTo(MCPRuntimeCache.CacheState.Ready), readyDetail);
                Assert.That(Directory.GetDirectories(plan.RuntimeRoot, Path.GetFileName(plan.RuntimeDirectory) + ".invalid-*").Length, Is.EqualTo(1));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void RuntimeBootstrap_DependencyHealthUsesCachedRuntimeOnly()
        {
            string root = Path.Combine(Path.GetTempPath(), "mcp-runtime-deps-" + Guid.NewGuid().ToString("N"));
            try
            {
                MCPRuntimeCache.Plan plan = CreateRuntimePlan(root);
                Assert.That(MCPRuntimeCache.TryEnsure(plan, out _, out string setupFailure), Is.True, setupFailure);
                Assert.That(MCPRuntimeCache.DependenciesReady(plan), Is.False);
                Directory.CreateDirectory(Path.GetDirectoryName(plan.DependencyPath));
                File.WriteAllText(plan.DependencyPath, "{}");
                Assert.That(MCPRuntimeCache.DependenciesReady(plan), Is.True);
                Assert.That(File.Exists(Path.Combine(plan.SourceDirectory, "node_modules", "@modelcontextprotocol", "sdk", "package.json")), Is.False);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        static MCPRuntimeCache.Plan CreateRuntimePlan(string root)
        {
            string source = Path.Combine(root, "source");
            string runtimeRoot = Path.Combine(root, "runtime");
            Directory.CreateDirectory(source);
            foreach (string fileName in new[] { "index.js", "registry.js", "commands.json", "package.json", "package-lock.json" })
            {
                File.WriteAllText(Path.Combine(source, fileName), fileName + " fixture");
            }

            var sourceInfo = new MCPRuntimeCache.Source(source, runtimeRoot, "1.2.3");
            Assert.That(MCPRuntimeCache.TryCreatePlan(sourceInfo, out MCPRuntimeCache.Plan plan, out string failure), Is.True, failure);
            StringAssert.StartsWith(runtimeRoot, plan.RuntimeDirectory);
            StringAssert.StartsWith("1.2.3-", Path.GetFileName(plan.RuntimeDirectory));
            return plan;
        }

        static T InvokeSetup<T>(string methodName, params object[] arguments)
        {
            MethodInfo method = typeof(MCPSetup).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null, methodName);
            return (T)method.Invoke(null, arguments);
        }

    }
}
