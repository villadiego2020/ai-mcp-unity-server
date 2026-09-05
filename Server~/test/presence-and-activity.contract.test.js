import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const testDirectory = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(testDirectory, "..", "..");

function read(relativePath) {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}

test("presence replacement retries only transient failures with bounded delay and cleanup", () => {
  const server = read("Editor/MCPServer.cs");

  assert.match(server, /const int PRESENCE_WRITE_ATTEMPTS = 3;/);
  assert.match(server, /const int PRESENCE_RETRY_DELAY_MS = 5;/);
  assert.match(server, /attempt <= PRESENCE_WRITE_ATTEMPTS/);
  assert.match(server, /IsTransientPresenceWriteFailure\(exception\)[\s\S]*attempt < PRESENCE_WRITE_ATTEMPTS/);
  assert.match(server, /Thread\.Sleep\(PRESENCE_RETRY_DELAY_MS \* attempt\)/);
  assert.match(server, /exception is IOException \|\| exception is UnauthorizedAccessException/);
  assert.match(server, /catch[\s\S]*File\.Exists\(temporaryPath\)[\s\S]*File\.Delete\(temporaryPath\)[\s\S]*throw;/);
});

test("presence warnings are throttled independently for each directory", () => {
  const server = read("Editor/MCPServer.cs");

  assert.match(server, /const double PRESENCE_WARNING_INTERVAL = 30\.0;/);
  assert.match(server, /Dictionary<string, double> _lastPresenceWarningByDirectory/);
  assert.match(server, /TryGetValue\(directory, out double lastWarning\)/);
  assert.match(server, /now - lastWarning < PRESENCE_WARNING_INTERVAL/);
  assert.match(server, /_lastPresenceWarningByDirectory\[directory\] = now;/);
});

test("inbound activity refreshes on thread-safe log revision and describes transport scope", () => {
  const handlers = read("Editor/MCPHandlers.cs");
  const window = read("Editor/MCPChatWindow.cs");
  const readme = read("README.md");

  assert.match(handlers, /get \{ lock \(Log\) \{ return Log\.Count; \} \}/);
  assert.match(handlers, /Interlocked\.Read\(ref _logRevision\)/);
  assert.match(handlers, /Interlocked\.Increment\(ref _logRevision\)/);
  assert.match(window, /if \(_activeTab == 2\)/);
  assert.match(window, /logRevision != _displayedMcpLogRevision/);
  assert.match(window, /MCPHandlers\.LogCount/);
  assert.match(window, /requests from Node\/TCP, Native, Pipeline and Editor/);
  assert.match(window, /built-in tools use their own permissions and logs/);
  assert.match(readme, /Native/);
});
