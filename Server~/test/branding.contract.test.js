import assert from "node:assert/strict";
import crypto from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(testDirectory, "..", "..");
const thisTestFile = fileURLToPath(import.meta.url);
const forbiddenBrandPatterns = [
  /DeltaMCP/i,
  /Delta[\s_-]+AI/i,
  /Delta-Project/i,
  /delta-unity/i,
];
const forbiddenProjectIdentityPatterns = [
  /ai-unity-mcp-server/i,
  new RegExp("github\\.com/(?:Smile-Codes|villadiego2020)/" + "mcp" + "bridge" + "(?:\\.git)?", "i"),
  new RegExp("[A-Za-z]:[/\\\\][^\\r\\n\"'`]*[/\\\\]com\\." + "mcp" + "bridge" + "[/\\\\]Server~", "i"),
  new RegExp("file:\\.\\.[/\\\\]\\.\\.[/\\\\]com\\." + "mcp" + "bridge" + "\\b", "i"),
];

function filesUnder(directory, predicate) {
  const output = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.name === "node_modules" || entry.name === "test") continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) output.push(...filesUnder(fullPath, predicate));
    else if (predicate(fullPath)) output.push(fullPath);
  }
  return output;
}

function maintainedTextFiles(directory) {
  const excludedDirectories = new Set([".git", ".agent-memory", "node_modules", "Library", "Temp", "Logs", "obj", "UserSettings"]);
  const output = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if (entry.isDirectory() && excludedDirectories.has(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      output.push(...maintainedTextFiles(fullPath));
      continue;
    }
    if (!entry.isFile()) continue;
    const content = fs.readFileSync(fullPath);
    if (!content.includes(0)) output.push(fullPath);
  }
  return output;
}

function commandContract(value) {
  if (Array.isArray(value)) return value.map(commandContract);
  if (!value || typeof value !== "object") return value;
  return Object.fromEntries(Object.entries(value)
    .filter(([key]) => !["_note", "description", "desc"].includes(key))
    .map(([key, child]) => [key, commandContract(child)]));
}

test("production and documentation contain only the canonical AI Unity MCP Server brand", () => {
  const files = [
    path.join(packageRoot, "package.json"),
    path.join(packageRoot, "README.md"),
    path.join(packageRoot, "CHANGELOG.md"),
    ...filesUnder(path.join(packageRoot, "Editor"), file => file.endsWith(".cs")),
    ...filesUnder(path.join(packageRoot, "Server~"), file => /\.(js|json)$/.test(file)),
    ...filesUnder(path.join(packageRoot, "Documentation~"), file => /\.(md|json|template)$/.test(file)),
  ];

  const violations = [];
  for (const file of files) {
    const content = fs.readFileSync(file, "utf8");
    for (const pattern of forbiddenBrandPatterns) {
      if (pattern.test(content)) {
        violations.push(`${path.relative(packageRoot, file)} matches ${pattern}`);
      }
    }
  }
  assert.deepEqual(violations, []);

  const packageManifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8"));
  const serverManifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "Server~", "package.json"), "utf8"));
  const serverLock = JSON.parse(fs.readFileSync(path.join(packageRoot, "Server~", "package-lock.json"), "utf8"));
  assert.equal(packageManifest.name, "com.villadiego.ai-mcp-unity-server");
  assert.equal(packageManifest.version, "2.0.0");
  assert.equal(packageManifest.displayName, "AI Unity MCP Server");
  assert.equal(packageManifest.author.name, "villadiego2020");
  assert.equal(serverManifest.name, "ai-mcp-unity-server");
  assert.equal(serverManifest.version, "2.0.0");
  assert.equal(serverLock.name, "ai-mcp-unity-server");
  assert.equal(serverLock.version, "2.0.0");
  assert.equal(serverLock.packages[""].name, "ai-mcp-unity-server");
  assert.equal(serverLock.packages[""].version, "2.0.0");
});

test("external project identity uses the canonical repository slug", () => {
  const violations = [];
  for (const file of maintainedTextFiles(packageRoot)) {
    if (path.resolve(file) === path.resolve(thisTestFile)) continue;
    const content = fs.readFileSync(file, "utf8");
    for (const pattern of forbiddenProjectIdentityPatterns) {
      if (pattern.test(content)) {
        violations.push(`${path.relative(packageRoot, file)} matches ${pattern}`);
      }
    }
  }
  assert.deepEqual(violations, []);

  const readme = fs.readFileSync(path.join(packageRoot, "README.md"), "utf8");
  assert.match(readme, /github\.com\/villadiego2020\/ai-mcp-unity-server\.git/);
  assert.match(readme, /"com\.villadiego\.ai-mcp-unity-server":\s*"2\.0\.0"/);
  assert.doesNotMatch(readme, /C:[/\\]Work[/\\]git/i);
});

test("canonical storage paths and MCP server key are wired consistently", () => {
  const registry = fs.readFileSync(path.join(packageRoot, "Server~", "registry.js"), "utf8");
  const bridge = fs.readFileSync(path.join(packageRoot, "Server~", "index.js"), "utf8");
  const editorServer = fs.readFileSync(path.join(packageRoot, "Editor", "MCPServer.cs"), "utf8");
  const setup = fs.readFileSync(path.join(packageRoot, "Editor", "MCPSetup.cs"), "utf8");
  const packagePaths = fs.readFileSync(path.join(packageRoot, "Editor", "MCPPackagePaths.cs"), "utf8");

  assert.match(registry, /\.AIUnityMCPServer["'],\s*["']instances/);
  assert.match(registry, /["']Library["'],\s*["']AIUnityMCPServer["'],\s*["']instances/);
  assert.match(bridge, /["']Library["'],\s*["']AIUnityMCPServer["']/);
  assert.match(editorServer, /Library["'],\s*["']AIUnityMCPServer["'],\s*["']instances/);
  assert.match(editorServer, /["']AIUnityMCPServer_WasRunning["']/);
  assert.match(setup, /ServerName\s*=\s*["']AIUnityMCPServer["']/);
  assert.match(packagePaths, /Path\.Combine\(localApplicationData,\s*["']AIUnityMCPServer["'],\s*["']runtime["']\)/);
});

test("all maintained repository text is English-only", () => {
  const violations = [];
  for (const file of maintainedTextFiles(packageRoot)) {
    const content = fs.readFileSync(file, "utf8");
    const match = /[\u0E00-\u0E7F]/u.exec(content);
    if (match) {
      const line = content.slice(0, match.index).split(/\r?\n/).length;
      violations.push(`${path.relative(packageRoot, file)}:${line}`);
    }
  }
  assert.deepEqual(violations, []);
});

test("translated command manifest preserves its executable contract", () => {
  const manifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "Server~", "commands.json"), "utf8"));
  const hash = crypto.createHash("sha256")
    .update(JSON.stringify(commandContract(manifest)))
    .digest("hex");
  assert.equal(hash, "0bd39ac04298f84d1795e94ba253d8508d78de76befde114df70b23512c6bbeb");
  assert.equal(manifest.commands.length, 73);
  assert.equal(new Set(manifest.commands.map(command => command.tool)).size, 73);
  assert.equal(new Set(manifest.commands.map(command => command.command)).size, 73);
  assert.equal(new Set(manifest.commands.map(command => command.path)).size, 73);

  const dispatcherSource = filesUnder(path.join(packageRoot, "Editor"), file => /^MCPHandlers(?:\.[^.]+)?\.cs$/.test(path.basename(file)))
    .map(file => fs.readFileSync(file, "utf8"))
    .join("\n");
  const missingRoutes = manifest.commands
    .map(command => command.path)
    .filter(route => !dispatcherSource.includes(`\"${route}\"`));
  assert.deepEqual(missingRoutes, []);
});

test("renamed runtime guide has no stale filename references", () => {
  const oldName = "runtime-inspection-th.md";
  assert.equal(fs.existsSync(path.join(packageRoot, "Documentation~", oldName)), false);
  const staleReferences = maintainedTextFiles(packageRoot)
    .filter(file => path.resolve(file) !== path.resolve(thisTestFile))
    .filter(file => fs.readFileSync(file, "utf8").includes(oldName))
    .map(file => path.relative(packageRoot, file));
  assert.deepEqual(staleReferences, []);
  assert.equal(fs.existsSync(path.join(packageRoot, "Documentation~", "runtime-inspection.md")), true);
});
