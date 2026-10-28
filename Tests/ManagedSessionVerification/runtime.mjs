import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { execFileSync } from "node:child_process";
import { fileURLToPath } from "node:url";
import { Client } from "../../Server~/node_modules/@modelcontextprotocol/sdk/dist/esm/client/index.js";
import { StdioClientTransport } from "../../Server~/node_modules/@modelcontextprotocol/sdk/dist/esm/client/stdio.js";

// Test-only fixture runner. The caller supplies an isolated test Editor port.
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
const project = process.env.CMM011_TEST_PROJECT;
const port = Number(process.env.CMM011_TEST_PORT);
const output = process.env.CMM011_TEST_OUTPUT;
assert(project && output && Number.isInteger(port) && port > 0, "Provide isolated project, port and evidence directory.");
const handlesPath = process.env.CMM011_TEST_HANDLES || path.join(process.env.LOCALAPPDATA || output, "AIUnityMCPServer", "VerificationSecrets", "CMM011-private-handles.json");
assert(!path.resolve(handlesPath).startsWith(path.resolve(output) + path.sep), "Private handles must stay outside public evidence.");
fs.mkdirSync(path.dirname(handlesPath), { recursive: true });
if (process.platform === "win32") {
  const sid = execFileSync("whoami", ["/user", "/fo", "csv", "/nh"], { encoding: "utf8" }).match(/S-1-\d+(?:-\d+)+/)?.[0];
  assert(sid, "Cannot establish current Windows user for private handle storage.");
  execFileSync("icacls", [path.dirname(handlesPath), "/inheritance:r", "/grant:r", "*" + sid + ":(OI)(CI)F"], { stdio: "pipe" });
}
fs.mkdirSync(output, { recursive: true });
const client = new Client({ name: "independent-managed-session-verifier", version: "1.0.0" });
const transport = new StdioClientTransport({ command: process.execPath, args: [path.join(repo, "Server~", "index.js")], cwd: project,
  env: { ...process.env, UNITY_MCP_PORT: String(port), UNITY_PROJECT_PATH: project }, stderr: "pipe" });
const evidence = { project, started: new Date().toISOString(), checks: [] };
const secretState = {};
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
const requestId = label => label + "-" + crypto.randomUUID();
const hash = file => crypto.createHash("sha256").update(fs.readFileSync(file)).digest("hex");
const note = (name, data = {}) => { const entry = { name, time: new Date().toISOString(), ...data }; evidence.checks.push(entry); console.log(JSON.stringify(entry)); fs.writeFileSync(path.join(output, "runtime-evidence.json"), JSON.stringify(evidence, null, 2)); };
function auth(session) { return { sessionId: session.sessionId, capability: session.capability }; }
async function call(name, args, allowError = false) {
  const raw = await client.callTool({ name, arguments: args });
  const text = raw.content?.find(item => item.type === "text")?.text;
  const result = JSON.parse(text);
  if (!allowError && result.error) throw new Error(name + ": " + result.code + ": " + result.error);
  return result;
}
async function poll(label, action, done, attempts = 1320) {
  for (let count = 0; count < attempts; count++) {
    const result = await action();
    if (done(result)) return result;
    await delay(1000);
  }
  throw new Error(label + " exceeded bounded verification deadline.");
}
async function command(session, run, action, extra = {}) {
  const queued = await call("unity_player", { ...auth(session), action, runId: run.runId, requestId: requestId(action), ...extra });
  const result = await poll(action, () => call("unity_player", { ...auth(session), action: "status", runId: run.runId, commandId: queued.commandId }), result => result.command?.state === "done", 40);
  assert(!result.command.error, result.command.error);
  return result.command;
}
try {
  await client.connect(transport);
  const connection = await call("unity_connection_status", {});
  assert.equal(connection.target.projectPath.replaceAll("\\", "/").toLowerCase(), path.resolve(project).replaceAll("\\", "/").toLowerCase(), "Refuse to test an unexpected Unity project.");
  if (process.env.CMM011_TEST_PID) assert.equal(connection.target.pid, Number(process.env.CMM011_TEST_PID), "Refuse an unexpected Unity process.");
  const source = "Assets/CMM011Verification/VerificationScene.cs";
  const marker = "Assets/CMM011Verification/Resources/SessionMarker.txt";
  const baseline = { source: hash(path.join(project, source)), marker: hash(path.join(project, marker)) };
  const a = await call("unity_session", { action: "register", label: "Verification A", requestId: requestId("register-a"), leaseSeconds: 7200 });
  const b = await call("unity_session", { action: "register", label: "Verification B", requestId: requestId("register-b"), leaseSeconds: 7200 });
  secretState.a = a; secretState.b = b;
  fs.writeFileSync(handlesPath, JSON.stringify(secretState, null, 2));
  for (const session of [a, b]) await poll("workspace ready", () => call("unity_session", { ...auth(session), action: "status" }), result => {
    if (result.state === "failed") throw new Error(result.failureDetail || result.error); return result.state === "ready";
  }, 180);
  assert.notEqual(a.workspacePath, b.workspacePath);
  for (const [file, from, to] of [[source, "BASE-CODE", "SESSION-A-CODE"], [marker, "BASE-ASSET", "SESSION-A-ASSET"]]) {
    const before = await call("unity_workspace", { ...auth(a), action: "read", path: file });
    assert(before.content.includes(from));
    await call("unity_workspace", { ...auth(a), action: "write", path: file, expectedHash: before.hash, content: before.content.replaceAll(from, to), requestId: requestId("write-a") });
    const unchanged = await call("unity_workspace", { ...auth(b), action: "read", path: file });
    assert(unchanged.content.includes(from) && !unchanged.content.includes(to));
  }
  assert.equal(hash(path.join(project, source)), baseline.source);
  assert.equal(hash(path.join(project, marker)), baseline.marker);
  const denied = await call("unity_session", { action: "status", sessionId: b.sessionId, capability: a.capability }, true);
  assert.equal(denied.code, "OWNER_DENIED");
  for (const badPath of ["Assets/../ProjectSettings/ProjectSettings.asset", "C:/Windows/win.ini", "Assets//invalid.txt"]) {
    const deniedPath = await call("unity_workspace", { ...auth(a), action: "read", path: badPath }, true);
    assert(deniedPath.error, "Invalid path was accepted: " + badPath);
  }
  note("source-asset-owner-path-isolation", { a: a.sessionId, b: b.sessionId, mainHashesUnchanged: true });
  const buildRequestA = { ...auth(a), action: "start", requestId: requestId("build-a") };
  const ba = await call("unity_player_build", buildRequestA);
  const duplicate = await call("unity_player_build", buildRequestA);
  assert.equal(duplicate.jobId, ba.jobId);
  const bb = await call("unity_player_build", { ...auth(b), action: "start", requestId: requestId("build-b") });
  secretState.buildA = ba; secretState.buildB = bb;
  fs.writeFileSync(handlesPath, JSON.stringify(secretState, null, 2));
  note("two-builds-queued", { a: ba.jobId, b: bb.jobId });
  let lastStates = "";
  await poll("two builds", async () => {
    const [sa, sb] = await Promise.all([call("unity_player_build", { ...auth(a), action: "status", jobId: ba.jobId }), call("unity_player_build", { ...auth(b), action: "status", jobId: bb.jobId })]);
    const jobs = [sa.builds[0], sb.builds[0]];
    assert(jobs.filter(job => job.state === "building" || job.state === "cancelling").length <= 1, "More than one worker lease is active.");
    const states = jobs.map(job => job.state).join("/");
    if (states !== lastStates) { note("build-states", { a: jobs[0].state, b: jobs[1].state }); lastStates = states; }
    for (const job of jobs) if (job.state === "failed" || job.state === "cancelled") throw new Error("Build " + job.jobId + ": " + (job.failureDetail || job.error) + " log=" + job.logPath);
    return jobs;
  }, jobs => jobs.every(job => job.state === "succeeded"));
  const ra = await call("unity_player", { ...auth(a), action: "start", jobId: ba.jobId, requestId: requestId("start-a") });
  const rb = await call("unity_player", { ...auth(b), action: "start", jobId: bb.jobId, requestId: requestId("start-b") });
  secretState.runA = ra; secretState.runB = rb;
  fs.writeFileSync(handlesPath, JSON.stringify(secretState, null, 2));
  for (const [session, run] of [[a, ra], [b, rb]]) await poll("Player ready", () => call("unity_player", { ...auth(session), action: "status", runId: run.runId }), result => result.runs[0].state === "running", 80);
  assert.notEqual(ra.pid, rb.pid);
  const left = JSON.parse((await command(a, ra, "input", { inputName: "move-left" })).value);
  const right = JSON.parse((await command(b, rb, "input", { inputName: "move-right" })).value);
  assert.equal(left.x, -1); assert.equal(right.x, 1);
  assert.equal(left.codeMarker, "SESSION-A-CODE"); assert.equal(left.assetMarker, "SESSION-A-ASSET");
  assert.equal(right.codeMarker, "BASE-CODE"); assert.equal(right.assetMarker, "BASE-ASSET");
  assert.notEqual(left.saveDirectory, right.saveDirectory);
  const captureA = await command(a, ra, "capture");
  const captureB = await command(b, rb, "capture");
  for (const frame of [captureA.screenshot, captureB.screenshot]) assert(fs.existsSync(frame) && fs.statSync(frame).size > 100, "Actual PNG absent.");
  note("two-real-players-opposing-input", { a: { runId: ra.runId, pid: ra.pid, ...left, screenshot: captureA.screenshot }, b: { runId: rb.runId, pid: rb.pid, ...right, screenshot: captureB.screenshot } });
  const tooLarge = await call("unity_player", { ...auth(a), action: "input", runId: ra.runId, requestId: requestId("oversized"), inputName: "state", inputValue: String.fromCharCode(0x4e00).repeat(32768) }, true);
  assert.equal(tooLarge.code, "COMMAND_LIMIT");
  assert.equal(JSON.parse((await command(a, ra, "input", { inputName: "state" })).value).x, -1);
  await call("unity_player", { ...auth(a), action: "close", runId: ra.runId });
  await poll("A closed", () => call("unity_player", { ...auth(a), action: "status", runId: ra.runId }), result => result.runs[0].state === "closed", 40);
  assert.equal(JSON.parse((await command(b, rb, "input", { inputName: "state" })).value).x, 1);
  const events = await call("unity_session", { ...auth(a), action: "events", afterSequence: 0 });
  assert.equal(events.events.filter(event => event.type === "player_closed" && event.runId === ra.runId).length, 1);
  assert.equal(events.events.find(event => event.type === "player_closed" && event.runId === ra.runId).reason, "owner_closed");
  note("owner-close-a-preserves-b", { events: events.events, bStillRunning: true, oversizedCommandRejectedWithoutPoisoning: true });
  note("runtime-verification-complete", { remainingRun: rb.runId, remainingPid: rb.pid, limits: "Frames require visual inspection; user/OS close and reload are separate checks." });
} catch (error) {
  note("verification-failed", { error: error.message });
  process.exitCode = 1;
} finally {
  await client.close();
  await transport.close();
}
