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
  const excludedDirectories = new Set([".git", "node_modules", "Library", "Temp", "Logs", "obj", "UserSettings"]);
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
  assert.equal(packageManifest.name, "com.mcpbridge");
  assert.equal(packageManifest.displayName, "AI Unity MCP Server");
  assert.equal(packageManifest.author.name, "AI Unity MCP Server");
  assert.equal(serverManifest.name, "ai-unity-mcp-server");
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
  assert.equal(hash, "845eb8d4bdbfd3c3dffdf31f023084b12b18c3be27ac78b1bcc5a98c75e87dbf");
  assert.equal(manifest.commands.length, 69);
  assert.equal(new Set(manifest.commands.map(command => command.tool)).size, 69);
  assert.equal(new Set(manifest.commands.map(command => command.command)).size, 69);
  assert.equal(new Set(manifest.commands.map(command => command.path)).size, 69);

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
