import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(testDirectory, "..", "..");
const releaseVerifier = path.join(packageRoot, "Tools~", "Release", "verify-release.mjs");

function read(relativePath) {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}

function readJson(relativePath) {
  return JSON.parse(read(relativePath));
}

test("release identities and versions agree across every runtime surface", () => {
  const manifest = readJson("package.json");
  const server = readJson("Server~/package.json");
  const lock = readJson("Server~/package-lock.json");
  const bridge = read("Server~/index.js");
  const handlers = read("Editor/MCPHandlers.cs");

  assert.equal(manifest.name, "com.villadiego.ai-mcp-unity-server");
  assert.equal(manifest.version, "2.0.2");
  assert.equal(server.name, "ai-mcp-unity-server");
  assert.equal(server.version, manifest.version);
  assert.equal(lock.name, server.name);
  assert.equal(lock.version, manifest.version);
  assert.equal(lock.packages[""].name, server.name);
  assert.equal(lock.packages[""].version, manifest.version);
  assert.match(bridge, /version:\s*"2\.0\.2"/);
  assert.match(handlers, /\\"version\\":\\"2\.0\.2\\"/);
});

test("release verifier accepts the exact tag and rejects a mismatched tag", () => {
  const accepted = spawnSync(process.execPath, [releaseVerifier, "--tag", "v2.0.2"], {
    cwd: packageRoot,
    encoding: "utf8",
  });
  assert.equal(accepted.status, 0, accepted.stderr || accepted.stdout);
  assert.match(accepted.stdout, /Release verification passed/);

  const rejected = spawnSync(process.execPath, [releaseVerifier, "--tag", "v2.0.1"], {
    cwd: packageRoot,
    encoding: "utf8",
  });
  assert.notEqual(rejected.status, 0);
  assert.match(rejected.stderr, /release tag must exactly match package version/);
});

test("release verifier scans its own module type without embedding stale identities", () => {
  const verifier = read("Tools~/Release/verify-release.mjs");
  assert.match(verifier, /["']\.mjs["']/);
  assert.doesNotMatch(verifier, new RegExp("MCP" + "Bridge", "i"));
  assert.doesNotMatch(verifier, new RegExp("com\\." + "mcp" + "bridge", "i"));
});

test("OpenUPM publication is tag-triggered and explicitly gated", () => {
  const workflow = read(".github/workflows/openupm.yml");
  assert.match(workflow, /tags:\s*\r?\n\s*- ['"]v\*\.\*\.\*['"]/);
  assert.match(workflow, /if:\s*vars\.OPENUPM_ENABLED == ['"]true['"]/);
  assert.match(workflow, /verify-release\.mjs --tag/);
  assert.match(workflow, /package:\s*com\.villadiego\.ai-mcp-unity-server/);
});

test("current README and concise changelog describe the 2.0.2 release", () => {
  const readme = read("README.md");
  const changelog = read("CHANGELOG.md");
  const currentRelease = changelog.split("## [2.0.1]")[0];

  assert.match(readme, /## Install version 2\.0\.2/);
  assert.match(readme, /"com\.villadiego\.ai-mcp-unity-server":\s*"2\.0\.2"/);
  assert.match(readme, /## Unity CLI and official Unity MCP coexistence/);
  assert.match(readme, /ai_mcp_list_commands/);
  assert.match(readme, /ai_mcp_dispatch/);
  assert.doesNotMatch(readme, /C:[/\\]Work[/\\]git/i);

  assert.match(currentRelease, /## \[2\.0\.2\] - 2026-09-05/);
  assert.match(currentRelease, /### Fixed/);
  assert.doesNotMatch(changelog, /^## \[Unreleased\]/m);
  assert.ok(currentRelease.split(/\r?\n/).length <= 35, "the current release notes should stay concise");
});

test("official Unity Pipeline exposes two adapters over all 73 dispatcher routes", () => {
  const pipeline = read("Editor/MCPHandlers.Pipeline.cs");
  const manifest = readJson("Server~/commands.json");
  const names = [...pipeline.matchAll(/\[CliCommand\(\s*"([^"]+)"/g)].map(match => match[1]);

  assert.deepEqual(names, ["ai_mcp_list_commands", "ai_mcp_dispatch"]);
  assert.equal(manifest.commands.length, 73);
  assert.equal(new Set(manifest.commands.map(command => command.path)).size, 73);
  assert.match(pipeline, /Dispatch\(command\.Trim\(\), request\.ToString\(Formatting\.None\)\)/);
  assert.match(pipeline, /writeCommandsAllowed/);
});
