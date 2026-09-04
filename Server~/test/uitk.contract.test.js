import assert from "node:assert/strict";
import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const serverDirectory = path.resolve(testDirectory, "..");
const packageRoot = path.resolve(serverDirectory, "..");
const serverEntry = path.join(serverDirectory, "index.js");
const manifest = JSON.parse(fs.readFileSync(path.join(serverDirectory, "commands.json"), "utf8"));
const expected = [
  ["unity_uitk_inspect", "uitk_inspect", "/uitk/inspect"],
  ["unity_uitk_validate", "uitk_validate", "/uitk/validate"],
  ["unity_uitk_apply", "uitk_apply", "/uitk/apply"],
  ["unity_uitk_playtest", "uitk_playtest", "/uitk/playtest"],
];

test("UI Toolkit manifest exposes exactly four unique bounded contracts", () => {
  const commands = manifest.commands.filter(command => command.tool.startsWith("unity_uitk_"));
  assert.deepEqual(commands.map(command => [command.tool, command.command, command.path]), expected);
  assert.equal(new Set(commands.map(command => command.tool)).size, 4);
  assert.equal(new Set(commands.map(command => command.command)).size, 4);
  assert.equal(new Set(commands.map(command => command.path)).size, 4);

  const [inspect, validate, apply, playtest] = commands;
  assert.equal(inspect.params.path.type, "string");
  assert.match(inspect.params.path.desc, /Canonical Assets\/\.\.\. path to a \.uxml or \.uss file/);
  assert.deepEqual(inspect.params.maxNodes, { type: "integer", opt: true, default: 250, min: 1, max: 2000, desc: "Maximum UXML nodes returned" });
  assert.deepEqual(inspect.params.maxDepth, { type: "integer", opt: true, default: 20, min: 1, max: 100, desc: "Maximum UXML depth returned" });
  assert.deepEqual(inspect.params.maxSelectors, { type: "integer", opt: true, default: 300, min: 1, max: 2000, desc: "Maximum USS selectors returned" });
  assert.equal(validate.params.maxIssues.max, 500);
  assert.equal(apply.params.changes.minItems, 1);
  assert.equal(apply.params.changes.maxItems, 8);
  assert.deepEqual(apply.params.mode.values, ["plan", "commit"]);
  assert.equal(apply.noRetry, true);
  assert.equal(playtest.noRetry, true);
  assert.deepEqual(playtest.params.mode.values, ["start", "status"]);
  assert.deepEqual(playtest.params.action.values, ["snapshot", "click", "set-text", "set-toggle", "focus"]);
  assert.equal(playtest.params.maxNodes.max, 1000);
});

test("UI Toolkit routes reach the dispatcher and use request-aware write gating", () => {
  const dispatcher = fs.readFileSync(path.join(packageRoot, "Editor", "MCPHandlers.cs"), "utf8");
  for (const [, , route] of expected) assert.match(dispatcher, new RegExp(`\\"${route}\\"\\s*=>`));
  assert.match(dispatcher, /UIToolkitRequestRequiresWrite\(path, body\)/);
  assert.match(dispatcher, /path == "\/uitk\/apply"[\s\S]*HasExplicitJsonString\(body, "mode", "plan"\)/);
  assert.match(dispatcher, /HasExplicitJsonString\(body, "mode", "status"\)/);
  assert.match(dispatcher, /HasExplicitJsonString\(body, "action", "snapshot"\)/);

  const bridge = fs.readFileSync(path.join(serverDirectory, "index.js"), "utf8");
  assert.match(bridge, /command\.noRetry \? 1 : undefined/);
  assert.match(bridge, /command\.command === "uitk_playtest" && response\?\.status === "done"/);
  assert.match(bridge, /response\?\.evidence\?\.screenshot/);
});

test("completed UI Toolkit playtest screenshot is returned as MCP image content", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "uitk-bridge-"));
  const home = path.join(root, "home");
  const project = path.join(root, "UnityProject");
  const registry = path.join(home, ".AIUnityMCPServer", "instances");
  const screenshot = path.join(root, "snapshot.png");
  fs.mkdirSync(path.join(project, "Assets"), { recursive: true });
  fs.mkdirSync(path.join(project, "ProjectSettings"), { recursive: true });
  fs.mkdirSync(registry, { recursive: true });
  fs.writeFileSync(screenshot, Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "base64"));

  const unity = http.createServer((request, response) => {
    request.resume();
    request.on("end", () => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(request.url === "/uitk/playtest"
        ? { ok: true, status: "done", evidence: { screenshot } }
        : { ok: true }));
    });
  });
  await new Promise((resolve, reject) => {
    unity.once("error", reject);
    unity.listen(0, "127.0.0.1", resolve);
  });

  let client;
  let transport;
  try {
    transport = new StdioClientTransport({
      command: process.execPath,
      args: [serverEntry],
      cwd: root,
      stderr: "pipe",
      env: { ...process.env, HOME: home, USERPROFILE: home, UNITY_PROJECT_PATH: project },
    });
    client = new Client({ name: "uitk-contract-tests", version: "1.0.0" });
    await client.connect(transport);
    fs.writeFileSync(path.join(registry, `${transport.pid}.json`), JSON.stringify({
      schemaVersion: 2,
      instanceId: "uitk-test",
      pid: transport.pid,
      processKind: "editor",
      label: "Main",
      project: "UnityProject",
      projectPath: project,
      port: unity.address().port,
      serverOn: true,
      heartbeatUnixMs: Date.now(),
    }));

    const result = await client.callTool({
      name: "unity_uitk_playtest",
      arguments: { mode: "status", runId: "run-1" },
    });
    assert.deepEqual(result.content.map(item => item.type), ["text", "image"]);
    assert.equal(result.content[1].mimeType, "image/png");
    assert.ok(result.content[1].data.length > 0);
  } finally {
    await client?.close().catch(() => {});
    await transport?.close().catch(() => {});
    await new Promise(resolve => unity.close(resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
});
