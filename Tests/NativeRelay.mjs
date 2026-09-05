import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { Client } from "../Server~/node_modules/@modelcontextprotocol/sdk/dist/esm/client/index.js";
import { StdioClientTransport } from "../Server~/node_modules/@modelcontextprotocol/sdk/dist/esm/client/stdio.js";

// Run only against the disposable Unity host prepared for native integration verification.
const [relay, projectPath, imageMode] = process.argv.slice(2);
assert.ok(relay && projectPath, "Usage: node Tests/NativeRelay.mjs <official-relay> <isolated-project>");
assert.match(path.basename(projectPath), /^ai-mcp-unity-cmm010-/);
const client = new Client({ name: "ai-mcp-native-verification", version: "1.0.0" });
const transport = new StdioClientTransport({ command: relay, args: ["--mcp", "--project-path", projectPath], stderr: "pipe" });
let stderr = "";
transport.stderr?.on("data", chunk => { stderr += chunk.toString(); });
const results = [];
function record(name, value) { results.push({ name, result: "PASS", evidence: value }); console.log(`PASS ${name}`); }
try {
  await client.connect(transport, { timeout: 30000 });
  let listed = await client.listTools({}, { timeout: 30000 });
  const manifest = JSON.parse(fs.readFileSync(new URL("../Server~/commands.json", import.meta.url), "utf8"));
  const names = new Set(listed.tools.map(tool => tool.name));
  for (const tool of manifest.commands) assert.ok(names.has(tool.tool), `Missing native tool ${tool.tool}`);
  assert.equal(names.size, listed.tools.length);
  const inspectSchema = listed.tools.find(tool => tool.name === "unity_uitk_inspect").inputSchema;
  assert.equal(inspectSchema.properties.maxNodes.type, "integer");
  assert.equal(inspectSchema.properties.maxNodes.maximum, 2000);
  record("native handshake and catalog", { total: names.size, shared: manifest.commands.length, maxNodes: inspectSchema.properties.maxNodes });
  const nodeClient = new Client({ name: "native-schema-parity", version: "1.0.0" });
  const nodeTransport = new StdioClientTransport({ command: process.execPath, args: [fileURLToPath(new URL("../Server~/index.js", import.meta.url))], cwd: projectPath, stderr: "pipe" });
  try {
    await nodeClient.connect(nodeTransport);
    const nodeTools = (await nodeClient.listTools()).tools;
    // Both implementations intentionally strip unknown keys. Compare all declared fields,
    // defaults, bounds and required lists; omit emitter-specific metadata.
    const canonical = value => {
      if (Array.isArray(value)) return value.map(canonical);
      if (value && typeof value === "object") return Object.fromEntries(Object.entries(value)
        .filter(([key, child]) => !["$schema", "additionalProperties"].includes(key) && !(key === "required" && child.length === 0) && !(key === "propertyNames" && JSON.stringify(child) === '{"type":"string"}')).sort(([a], [b]) => a.localeCompare(b)).map(([key, child]) => [key, canonical(child)]));
      return value;
    };
    for (const entry of manifest.commands) {
      const native = listed.tools.find(tool => tool.name === entry.tool).inputSchema;
      const node = nodeTools.find(tool => tool.name === entry.tool).inputSchema;
      assert.deepEqual(canonical(native), canonical(node), `Schema differs for ${entry.tool}`);
    }
    record("native versus real Node MCP schema parity", { tools: manifest.commands.length });
  } finally { await nodeClient.close(); }
  const call = (name, args) => client.callTool({ name, arguments: args }, undefined, { timeout: 30000 });
  const ping = await call("unity_ping", {});
  assert.ok(!ping.isError, JSON.stringify(ping));
  assert.match(JSON.stringify(ping), /2\.1\.0/);
  assert.match(JSON.stringify(ping), /"status":\s*"ok"|\\"status\\":\\"ok\\"/);
  record("native ping", ping);
  const inspected = await call("unity_uitk_inspect", { path: "Assets/RelaySentinel.uxml" });
  assert.ok(!inspected.isError, JSON.stringify(inspected));
  assert.match(JSON.stringify(inspected), /relay-sentinel-button/);
  record("native UI Toolkit inspection", inspected);
  for (const [name, args] of [
    ["unity_uitk_inspect", { path: "Assets/RelaySentinel.uxml", maxNodes: 1.5 }],
    ["unity_set_transform", { name: "RelaySentinelCamera", set: "pos", px: 100 }],
  ]) {
    const error = await call(name, args);
    assert.equal(error.isError, true, JSON.stringify(error));
    record(`native failure envelope ${name}`, error);
  }
  const image = await call("unity_capture_screenshot", { view: "game", width: 64, height: 64, overlay: false });
  assert.ok(!image.isError, JSON.stringify(image));
  const png = image.content?.find(item => item.type === "image");
  if (imageMode === "--allow-local-image-fallback") {
    const delivery = image.structuredContent?.data?.imageDelivery;
    assert.equal(delivery?.mode, "local-file", JSON.stringify(image));
    assert.equal(delivery.verified, true);
    assert.equal(delivery.mimeType, "image/png");
    const relative = path.relative(projectPath, delivery.path);
    assert.ok(!relative.startsWith("..") && !path.isAbsolute(relative));
    const bytes = fs.readFileSync(delivery.path);
    assert.equal(bytes.length, delivery.bytes);
    assert.equal(bytes.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
    assert.equal(bytes.readUInt32BE(16), 64);
    assert.equal(bytes.readUInt32BE(20), 64);
    record("native screenshot verified local PNG fallback", { delivery, inlineImageAvailable: Boolean(png), limitation: "Official relay drops rich image blocks; local-file delivery does not prove inline model vision." });
  } else {
    // This strict mode retains the original capability check that failed with the official relay.
    assert.ok(png, JSON.stringify(image));
    assert.equal(png.mimeType, "image/png");
    const bytes = Buffer.from(png.data, "base64");
    assert.equal(bytes.subarray(0, 8).toString("hex"), "89504e470d0a1a0a");
    record("native screenshot image content", { mimeType: png.mimeType, bytes: bytes.length });
  }
} catch (error) {
  results.push({ name: "remaining relay verification", result: "FAIL", evidence: error.message });
  console.error(error.stack || error);
  console.error(stderr.slice(-6000));
  process.exitCode = 1;
} finally {
  await client.close();
  fs.writeFileSync(path.join(projectPath, "relay-results.json"), JSON.stringify(results, null, 2));
}
