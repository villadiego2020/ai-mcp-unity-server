import assert from "node:assert/strict";
import fs from "node:fs";
import crypto from "node:crypto";
import http from "node:http";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";
import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { LoggingMessageNotificationSchema } from "@modelcontextprotocol/sdk/types.js";

const serverDirectory = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const commands = JSON.parse(fs.readFileSync(path.join(serverDirectory, "commands.json"), "utf8")).commands;

test("four managed tools publish bounded ownership and lifecycle contracts", () => {
  const managed = commands.filter(command => command.path.startsWith("/managed/"));
  assert.deepEqual(managed.map(command => command.tool), ["unity_session", "unity_workspace", "unity_player_build", "unity_player"]);
  for (const command of managed) {
    assert.equal(command.noRetry, true);
    assert.equal(command.params.capability.max, 128);
    assert.equal(command.params.sessionId.max, 64);
    assert.equal(command.params.action.type, "enum");
  }
  const workspace = managed.find(command => command.tool === "unity_workspace");
  assert.equal(workspace.params.content.max, 262144);
  assert.equal(workspace.params.expectedHash.max, 64);
  assert.ok(managed.find(command => command.tool === "unity_session").params.action.values.includes("events"));
});

test("managed schemas reach Node discovery and completed Player frames return inline PNG", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "mcp-managed-tools-"));
  const screenshot = path.join(root, "player.png");
  fs.writeFileSync(screenshot, Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=", "base64"));
  const received = [];
  const unity = http.createServer((request, response) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", chunk => { body += chunk; });
    request.on("end", () => {
      received.push({ path: request.url, body: JSON.parse(body) });
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(request.url === "/managed/player"
        ? { runs: [], command: { state: "done", runId: "run-fixture", screenshot } }
        : { sessionId: "session-fixture", state: "snapshotting", capability: "fixture-capability" }));
    });
  });
  await new Promise(resolve => unity.listen(0, "127.0.0.1", resolve));
  const transport = new StdioClientTransport({
    command: process.execPath, args: [path.join(serverDirectory, "index.js")], cwd: root, stderr: "pipe",
    env: { ...process.env, HOME: root, USERPROFILE: root, UNITY_MCP_PORT: String(unity.address().port) },
  });
  const client = new Client({ name: "managed-contract-tests", version: "1.0.0" });
  try {
    await client.connect(transport);
    const listing = await client.listTools();
    for (const command of commands.filter(item => item.path.startsWith("/managed/"))) {
      const published = listing.tools.find(item => item.name === command.tool);
      assert.deepEqual(published.inputSchema.properties.action.enum, command.params.action.values);
    }
    await client.callTool({ name: "unity_session", arguments: { action: "register", label: "Fixture", requestId: "register-fixture" } });
    assert.equal(received[0].path, "/managed/session");
    assert.equal(received[0].body.leaseSeconds, 1800);
    const result = await client.callTool({ name: "unity_player", arguments: { action: "status", sessionId: "session-fixture", capability: "fixture-capability", runId: "run-fixture", commandId: "command-fixture" } });
    assert.deepEqual(result.content.map(item => item.type), ["text", "image"]);
    assert.equal(result.content[1].mimeType, "image/png");
  } finally {
    await client.close(); await transport.close();
    unity.closeAllConnections();
    await new Promise(resolve => unity.close(resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("connected owners receive closure notifications without polling and never receive another owner's event", async () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "mcp-owner-notifications-"));
  const project = path.join(root, "UnityProject");
  fs.mkdirSync(path.join(project, "Assets"), { recursive: true });
  fs.mkdirSync(path.join(project, "ProjectSettings"), { recursive: true });
  const registryDirectory = path.join(project, "Library", "AIUnityMCPServer", "managed-sessions");
  fs.mkdirSync(registryDirectory, { recursive: true });
  const registryPath = path.join(registryDirectory, "registry.json");
  const capabilities = { A: "a".repeat(64), B: "b".repeat(64) };
  const state = { schemaVersion: 1, sessions: ["A", "B"].map(label => ({
    id: `session-${label}`, label, state: "ready", leaseUntil: Date.now() + 60000,
    capabilityHash: crypto.createHash("sha256").update(capabilities[label]).digest("hex"), events: [],
  })), runs: [] };
  const save = () => {
    fs.writeFileSync(registryPath + ".new", JSON.stringify(state));
    fs.renameSync(registryPath + ".new", registryPath);
  };
  save();
  const requests = [];
  const unity = http.createServer((request, response) => {
    let body = "";
    request.setEncoding("utf8"); request.on("data", chunk => { body += chunk; });
    request.on("end", () => {
      const args = JSON.parse(body); requests.push({ path: request.url, action: args.action });
      const label = args.action === "register" ? args.label : args.sessionId?.slice(-1);
      const session = state.sessions.find(item => item.label === label);
      const allowed = session && (args.action === "register" || args.capability === capabilities[label]);
      response.writeHead(200, { "Content-Type": "application/json" });
      response.end(JSON.stringify(allowed ? { sessionId: session.id, state: "ready", leaseUntilUnixMs: session.leaseUntil,
        ...(args.action === "register" ? { capability: capabilities[label] } : {}) } : { error: "Owner denied", code: "OWNER_DENIED" }));
    });
  });
  await new Promise(resolve => unity.listen(0, "127.0.0.1", resolve));
  const connections = [];
  const received = { A: [], B: [] };
  const pending = {};
  const waitMessage = label => new Promise((resolve, reject) => {
    const timer = setTimeout(() => reject(new Error(`No owner ${label} notification received`)), 4000);
    pending[label] = value => { clearTimeout(timer); resolve(value); };
  });
  try {
    for (const label of ["A", "B"]) {
      const home = path.join(root, label); fs.mkdirSync(home, { recursive: true });
      const transport = new StdioClientTransport({ command: process.execPath, args: [path.join(serverDirectory, "index.js")], cwd: root, stderr: "pipe",
        env: { ...process.env, HOME: home, USERPROFILE: home, UNITY_MCP_PORT: String(unity.address().port) } });
      const client = new Client({ name: `owner-${label}`, version: "1.0.0" });
      client.setNotificationHandler(LoggingMessageNotificationSchema, notification => {
        received[label].push(notification.params.data); pending[label]?.(notification.params.data); delete pending[label];
      });
      await client.connect(transport); connections.push({ label, client, transport });
      const presence = path.join(home, ".AIUnityMCPServer", "instances"); fs.mkdirSync(presence, { recursive: true });
      fs.writeFileSync(path.join(presence, `${transport.pid}.json`), JSON.stringify({ schemaVersion: 2, instanceId: "owner-fixture", pid: transport.pid,
        processKind: "editor", label: "Main", projectPath: project, port: unity.address().port, serverOn: true, heartbeatUnixMs: Date.now() }));
      const result = await client.callTool({ name: "unity_session", arguments: { action: "register", label, requestId: `register-${label}` } });
      assert.match(JSON.parse(result.content[0].text).notificationDelivery, /notifications\/message/);
    }
    const denied = await connections[1].client.callTool({ name: "unity_session", arguments: { action: "status", sessionId: "session-A", capability: capabilities.B } });
    assert.equal(JSON.parse(denied.content[0].text).code, "OWNER_DENIED");
    const first = waitMessage("A");
    state.sessions[0].events.push({ sequence: 1, time: Date.now(), type: "player_closed", runId: "run-A", jobId: "build-A", reason: "user_closed" });
    save();
    assert.equal((await first).runId, "run-A");
    await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(received.B.length, 0);
    save(); await new Promise(resolve => setTimeout(resolve, 150));
    assert.equal(received.A.length, 1, "atomic rewrites must not duplicate an event");
    const second = waitMessage("B");
    state.sessions[1].events.push({ sequence: 1, time: Date.now(), type: "player_closed", runId: "run-B", jobId: "build-B", reason: "owner_closed" });
    save(); assert.equal((await second).runId, "run-B");
    assert.equal(received.A.length, 1);
    assert.equal(requests.length, 3, "notifications must not generate health or event-poll HTTP requests");
    const publicMessages = JSON.stringify(received);
    assert.equal(publicMessages.includes(capabilities.A), false);
    assert.equal(publicMessages.includes(capabilities.B), false);
    assert.equal(publicMessages.includes("capabilityHash"), false);
  } finally {
    for (const { client, transport } of connections) { await client.close(); await transport.close(); }
    unity.closeAllConnections(); await new Promise(resolve => unity.close(resolve));
    fs.rmSync(root, { recursive: true, force: true });
  }
});
