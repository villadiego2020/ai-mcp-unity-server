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
const serverEntry = path.join(serverDirectory, "index.js");
const commands = JSON.parse(fs.readFileSync(path.join(serverDirectory, "commands.json"), "utf8")).commands;

function parseToolResult(result) {
  return JSON.parse(result.content.find(item => item.type === "text").text);
}

async function startFakeUnity() {
  const server = http.createServer((request, response) => {
    request.resume();
    request.on("end", () => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ ok: true, port: server.address().port, path: request.url }));
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolve);
  });
  return server;
}

async function stopServer(server) {
  if (!server) return;
  await new Promise(resolve => server.close(resolve));
}

test("MCP handshake exposes connection tools and reconnects an offline Editor without changing its saved permission", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "mcp-bridge-"));
  const home = path.join(root, "home");
  const project = path.join(root, "UnityProject");
  const sharedRegistry = path.join(home, ".AIUnityMCPServer", "instances");
  const requestDirectory = path.join(project, "Library", "AIUnityMCPServer");
  const customConfig = path.join(home, ".codex", "config.toml");
  fs.mkdirSync(path.join(project, "Assets"), { recursive: true });
  fs.mkdirSync(path.join(project, "ProjectSettings"), { recursive: true });
  fs.mkdirSync(sharedRegistry, { recursive: true });
  fs.mkdirSync(requestDirectory, { recursive: true });
  fs.mkdirSync(path.dirname(customConfig), { recursive: true });
  const originalConfig = "[mcp_servers.unityMCP]\ncommand = \"official-unity-mcp\"\n";
  fs.writeFileSync(customConfig, originalConfig);

  let fakeUnity = null;
  let reboundUnity = null;
  let client = null;
  let transport = null;
  try {
    fakeUnity = await startFakeUnity();
    transport = new StdioClientTransport({
      command: process.execPath,
      args: [serverEntry],
      cwd: root,
      stderr: "pipe",
      env: {
        ...process.env,
        HOME: home,
        USERPROFILE: home,
        UNITY_PROJECT_PATH: project,
      },
    });
    client = new Client({ name: "mcp-bridge-tests", version: "1.0.0" });
    await client.connect(transport);

    const tools = await client.listTools();
    const toolNames = new Set(tools.tools.map(tool => tool.name));
    assert.equal(tools.tools.length, commands.length + 5);
    for (const name of ["unity_connection_status", "unity_connect", "unity_list_instances", "unity_ping"]) {
      assert.equal(toolNames.has(name), true, `${name} must be registered`);
    }

    const presencePath = path.join(sharedRegistry, `${transport.pid}.json`);
    const writePresence = (port, serverOn) => fs.writeFileSync(presencePath, JSON.stringify({
      schemaVersion: 2,
      instanceId: "unity-test-project",
      pid: transport.pid,
      processKind: "editor",
      label: "Main",
      project: "UnityProject",
      projectPath: project,
      port,
      serverOn,
      heartbeatUnixMs: Date.now(),
      startedAtUnixMs: Date.now(),
      packageVersion: "test",
    }));

    writePresence(fakeUnity.address().port, false);
    const initialStatus = parseToolResult(await client.callTool({ name: "unity_connection_status", arguments: {} }));
    assert.equal(initialStatus.status, "offline");

    const activate = setTimeout(() => writePresence(fakeUnity.address().port, true), 150);
    const connected = parseToolResult(await client.callTool({
      name: "unity_connect",
      arguments: { target: "unity-test-project", timeoutSeconds: 3 },
    }));
    clearTimeout(activate);
    assert.equal(connected.status, "connected");
    assert.match(connected.writeGate, /^OFF after remote start/);

    const startRequest = JSON.parse(fs.readFileSync(path.join(requestDirectory, "request-start"), "utf8"));
    assert.equal(startRequest.instanceId, "unity-test-project");
    assert.equal(startRequest.preserveWritePreference, true);
    assert.equal(startRequest.readOnly, undefined);

    reboundUnity = await startFakeUnity();
    await stopServer(fakeUnity);
    fakeUnity = null;
    writePresence(reboundUnity.address().port, true);

    const ping = parseToolResult(await client.callTool({ name: "unity_ping", arguments: {} }));
    assert.equal(ping.ok, true);
    assert.equal(ping.port, reboundUnity.address().port);
    assert.equal(ping.path, "/ping");

    const reboundStatus = parseToolResult(await client.callTool({ name: "unity_connection_status", arguments: {} }));
    assert.equal(reboundStatus.status, "connected");
    assert.equal(reboundStatus.target.port, reboundUnity.address().port);
    assert.equal(fs.readFileSync(customConfig, "utf8"), originalConfig);
  } finally {
    await client?.close().catch(() => {});
    await transport?.close().catch(() => {});
    await stopServer(fakeUnity);
    await stopServer(reboundUnity);
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("queued requests can wait beyond eight seconds and uncertain mutations are never replayed", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "mcp-queue-bridge-"));
  const calls = new Map();
  const timers = new Set();
  const fakeUnity = http.createServer((request, response) => {
    request.resume();
    request.on("end", () => {
      const count = (calls.get(request.url) || 0) + 1;
      calls.set(request.url, count);
      if (request.url === "/gameobject/delete" || (request.url === "/scene/list" && count === 1)) {
        response.destroy();
        return;
      }
      const reply = () => {
        response.writeHead(200, { "Content-Type": "application/json" });
        response.end(JSON.stringify({ ok: true }));
      };
      if (request.url === "/gameobject/create") {
        const timer = setTimeout(() => { timers.delete(timer); reply(); }, 8500);
        timers.add(timer);
      } else reply();
    });
  });
  await new Promise(resolve => fakeUnity.listen(0, "127.0.0.1", resolve));
  const transport = new StdioClientTransport({
    command: process.execPath,
    args: [serverEntry],
    cwd: root,
    stderr: "pipe",
    env: { ...process.env, HOME: root, USERPROFILE: root, UNITY_MCP_PORT: String(fakeUnity.address().port) },
  });
  const client = new Client({ name: "mcp-queue-tests", version: "1.0.0" });
  try {
    await client.connect(transport);
    const delayed = parseToolResult(await client.callTool({ name: "unity_create_gameobject", arguments: { name: "QueueFixture" } }));
    assert.equal(delayed.ok, true);
    assert.equal(calls.get("/gameobject/create"), 1, "a queued write must complete without timeout replay");

    const uncertain = parseToolResult(await client.callTool({ name: "unity_delete_gameobject", arguments: { name: "QueueFixture" } }));
    assert.equal(uncertain.code, "EXECUTION_OUTCOME_UNKNOWN");
    assert.equal(uncertain.retryable, false);
    assert.equal(calls.get("/gameobject/delete"), 1, "a connection loss after dispatch must not replay a mutation");

    const recoveredRead = parseToolResult(await client.callTool({ name: "unity_scene_list", arguments: {} }));
    assert.equal(recoveredRead.ok, true);
    assert.equal(calls.get("/scene/list"), 2, "safe reads retain bounded reconnect retries");
  } finally {
    for (const timer of timers) clearTimeout(timer);
    await client.close();
    await transport.close();
    fakeUnity.closeAllConnections();
    await stopServer(fakeUnity);
    fs.rmSync(root, { recursive: true, force: true });
  }
});
