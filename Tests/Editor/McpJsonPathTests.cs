using System.IO;
using NUnit.Framework;

namespace AIUnityMCPServer.Tests
{
    // Guards the two path rules behind <project>/.mcp.json (MCPServer.EnsureMcpJson):
    // where the args path points, and which files this package is allowed to rewrite.
    public class McpJsonPathTests
    {
        const string ProjectRoot = "C:/Games/MyProject";

        // ── McpArgsPath ───────────────────────────────────────────────────
        [Test]
        public void McpArgsPath_EmbeddedPackage_IsProjectRelative()
        {
            string entry = ProjectRoot + "/Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js";
            Assert.AreEqual("./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js", MCPServer.McpArgsPath(entry, ProjectRoot));
        }

        [Test]
        public void McpArgsPath_PackageOutsideProject_IsAbsoluteWithForwardSlashes()
        {
            string entry = "C:/Work/git/ai-mcp-unity-server/Server~/index.js";
            string args = MCPServer.McpArgsPath(entry, ProjectRoot);
            Assert.IsTrue(Path.IsPathRooted(args), args);
            Assert.IsFalse(args.Contains("\\"), args);
            Assert.IsTrue(args.EndsWith("/ai-mcp-unity-server/Server~/index.js"), args);
        }

        [Test]
        public void McpArgsPath_ProjectRootWithTrailingSeparator_StillRelative()
        {
            string entry = ProjectRoot + "/Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js";
            Assert.AreEqual("./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js", MCPServer.McpArgsPath(entry, ProjectRoot + "/"));
        }

        // ── GeneratedMcpJson ──────────────────────────────────────────────
        [Test]
        public void GeneratedMcpJson_KeepsArgsPathIntact()
        {
            string args = "C:/path with spaces/ai-mcp-unity-server/Server~/index.js";
            string generated = MCPServer.GeneratedMcpJson(args);
            StringAssert.Contains($"\"{args}\"", generated);
            StringAssert.Contains("\"AIUnityMCPServer\"", generated);
        }

        [Test]
        public void GeneratedMcpJson_IsRecognizedAsOwnedByThisPackage()
        {
            Assert.IsTrue(MCPServer.IsGeneratedByThisPackage(MCPServer.GeneratedMcpJson("./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js")));
        }

        [Test]
        public void IsGeneratedByThisPackage_FormerUnityKey_IsNotOwned()
        {
            string former = "{\n  \"mcpServers\": {\n    \"unity\": {\n      \"command\": \"node\",\n"
                          + "      \"args\": [\"./other-server/index.js\"]\n    }\n  }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(former));
        }

        [Test]
        public void IsGeneratedByThisPackage_CrlfLineEndings_IsOwned()
        {
            string crlf = MCPServer.GeneratedMcpJson("./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js").Replace("\n", "\r\n");
            Assert.IsTrue(MCPServer.IsGeneratedByThisPackage(crlf));
        }

        [Test]
        public void IsGeneratedByThisPackage_FileWithAnotherServer_IsNotOwned()
        {
            string userFile = "{\n  \"mcpServers\": {\n    \"postgres\": { \"command\": \"npx\", \"args\": [\"pg-mcp\"] },\n"
                            + "    \"AIUnityMCPServer\": { \"command\": \"node\", \"args\": [\"./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js\"] }\n  }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(userFile));
        }

        [Test]
        public void IsGeneratedByThisPackage_FileWithExtraKeys_IsNotOwned()
        {
            string userFile = "{\n  \"mcpServers\": {\n    \"AIUnityMCPServer\": { \"command\": \"node\", \"args\": [\"./x.js\"] }\n  },\n"
                            + "  \"permissions\": { \"allow\": [\"*\"] }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(userFile));
        }

        [Test]
        public void IsGeneratedByThisPackage_UnityEntryWithEnvBlock_IsNotOwned()
        {
            string withEnv = "{\n  \"mcpServers\": {\n    \"AIUnityMCPServer\": { \"command\": \"node\", \"env\": { \"UNITY_MCP_PORT\": \"23457\" },\n"
                           + "      \"args\": [\"./Packages/com.villadiego.ai-mcp-unity-server/Server~/index.js\"] }\n  }\n}\n";
            Assert.IsFalse(MCPServer.IsGeneratedByThisPackage(withEnv));
        }
    }
}
