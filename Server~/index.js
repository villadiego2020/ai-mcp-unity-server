import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import fs from "fs";
import path from "path";
import { fileURLToPath } from "url";
import {
  instanceDirs,
  isReachableInstance,
  listInstances,
  preferredProjectRoot,
  resolveInstance,
} from "./registry.js";

const TIMEOUT_MS = 8000;
const MAX_RETRIES = 8;
const RETRY_DELAY_MS = 500;
const MAX_RETRY_DELAY_MS = 2000;
const DEFAULT_CONNECT_TIMEOUT_SECONDS = 10;
const COMMANDS_PATH = path.join(path.dirname(fileURLToPath(import.meta.url)), "commands.json");

let selectedTarget = null;

const sleep = milliseconds => new Promise(resolve => setTimeout(resolve, milliseconds));

function fixedPort() {
  if (!process.env.UNITY_MCP_PORT) return null;
  const port = Number(process.env.UNITY_MCP_PORT);
  return Number.isInteger(port) && port > 0 && port <= 65535 ? port : Number.NaN;
}

function targetIdentity(instance) {
  return {
    instanceId: instance.instanceId || "",
    pid: instance.pid || 0,
    port: instance.port || 0,
    label: instance.label || "",
    projectPath: instance.projectPath || "",
  };
}

function resolveTarget(options = {}) {
  const configuredPort = fixedPort();
  if (Number.isNaN(configuredPort)) {
    return {
      error: {
        code: "INVALID_CONFIG",
        error: `UNITY_MCP_PORT must be an integer from 1 to 65535, got '${process.env.UNITY_MCP_PORT}'.`,
        action: "Fix or remove UNITY_MCP_PORT, then restart the MCP client session.",
      },
    };
  }

  const instances = listInstances();
  if (configuredPort) {
    const registered = instances.find(instance => instance.port === configuredPort);
    return {
      instance: registered || {
        instanceId: `fixed-port-${configuredPort}`,
        label: "Fixed port",
        project: "",
        projectPath: "",
        pid: 0,
        port: configuredPort,
        serverOn: true,
        stale: false,
      },
      source: "UNITY_MCP_PORT",
    };
  }

  return resolveInstance(instances, {
    target: options.target,
    selected: selectedTarget,
    preferredRoot: preferredProjectRoot(),
    requireServerOn: !!options.requireServerOn,
  });
}

async function fetchUnity(instance, request) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), request.timeoutMs);
  try {
    const response = await fetch(`http://127.0.0.1:${instance.port}${request.path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request.body),
      signal: controller.signal,
    });
    const text = await response.text();
    try {
      return JSON.parse(text);
    } catch {
      return { raw: text, httpStatus: response.status };
    }
  } finally {
    clearTimeout(timer);
  }
}

async function callUnity(requestPath, body = {}, options = {}) {
  const timeoutMs = options.timeoutMs ?? TIMEOUT_MS;
  const maxRetries = options.maxRetries ?? MAX_RETRIES;
  let lastFailure = null;
  let lastInstance = null;

  for (let attempt = 1; attempt <= maxRetries; attempt++) {
    const resolved = resolveTarget({ requireServerOn: true });
    if (resolved.error) {
      if (["AMBIGUOUS", "INVALID_CONFIG"].includes(resolved.error.code)) return resolved.error;
      lastFailure = resolved.error;
    } else {
      lastInstance = resolved.instance;
      try {
        return await fetchUnity(resolved.instance, { path: requestPath, body, timeoutMs });
      } catch (error) {
        lastFailure = {
          code: error.name === "AbortError" ? "TIMEOUT" : "CONNECTION_FAILED",
          error: error.message,
        };
      }
    }

    if (attempt < maxRetries) {
      const delay = Math.min(RETRY_DELAY_MS * attempt, MAX_RETRY_DELAY_MS);
      console.error(`[AI Unity MCP Server] ${lastFailure?.code || "connection failed"} → re-discover ${attempt}/${maxRetries - 1} in ${delay}ms`);
      await sleep(delay);
    }
  }

  return {
    code: lastFailure?.code || "CONNECTION_FAILED",
    error: lastFailure?.error || "AI Unity MCP Server did not respond.",
    action: "Call unity_connection_status, then unity_connect. Pass an exact target if status is AMBIGUOUS.",
    lastTarget: lastInstance ? publicInstance(lastInstance) : undefined,
  };
}

function loadCommands() {
  try {
    let text = fs.readFileSync(COMMANDS_PATH, "utf8");
    if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
    return JSON.parse(text).commands || [];
  } catch (error) {
    console.error(`[AI Unity MCP Server] Could not load commands.json: ${error.message}`);
    return [];
  }
}

function toZodShape(parameters) {
  const shape = {};
  for (const [key, parameter] of Object.entries(parameters || {})) {
    let schema;
    switch (parameter.type) {
      case "number": schema = z.number(); break;
      case "integer": schema = z.number().int(); break;
      case "boolean": schema = z.boolean(); break;
      case "enum": schema = z.enum(parameter.values); break;
      case "number[]": schema = z.array(z.number()); break;
      case "object[]": schema = z.array(z.record(z.any())); break;
      default: schema = z.string();
    }
    if (Object.prototype.hasOwnProperty.call(parameter, "min")) schema = schema.min(parameter.min);
    if (Object.prototype.hasOwnProperty.call(parameter, "max")) schema = schema.max(parameter.max);
    if (Object.prototype.hasOwnProperty.call(parameter, "minItems")) schema = schema.min(parameter.minItems);
    if (Object.prototype.hasOwnProperty.call(parameter, "maxItems")) schema = schema.max(parameter.maxItems);
    if (parameter.desc) schema = schema.describe(parameter.desc);
    if (Object.prototype.hasOwnProperty.call(parameter, "default")) schema = schema.optional().default(parameter.default);
    else if (parameter.opt) schema = schema.optional();
    shape[key] = schema;
  }
  return shape;
}

function publicInstance(instance) {
  return {
    schemaVersion: instance.schemaVersion,
    instanceId: instance.instanceId || undefined,
    label: instance.label,
    project: instance.project,
    projectPath: instance.projectPath,
    pid: instance.pid,
    port: instance.port,
    serverOn: !!instance.serverOn,
    stale: !!instance.stale,
    heartbeatAgeMs: Number.isFinite(instance.heartbeatAgeMs) ? Math.round(instance.heartbeatAgeMs) : undefined,
    packageVersion: instance.packageVersion,
  };
}

function textResult(value) {
  return { content: [{ type: "text", text: JSON.stringify(value, null, 2) }] };
}

async function probe(instance, timeoutMs = 1200) {
  try {
    const response = await fetchUnity(instance, { path: "/ping", body: {}, timeoutMs });
    return { reachable: !response?.error, response };
  } catch (error) {
    return {
      reachable: false,
      error: error.name === "AbortError" ? "probe timed out" : error.message,
    };
  }
}

function writeStartRequest(instance) {
  if (!instance.projectPath) {
    return {
      code: "NO_PROJECT_PATH",
      error: `Unity Editor '${instance.label}' has no projectPath, so it cannot be started remotely.`,
      action: "Start the server from AI Unity MCP Server → Server → Start in that Editor.",
    };
  }

  try {
    const directory = path.join(instance.projectPath, "Library", "AIUnityMCPServer");
    fs.mkdirSync(directory, { recursive: true });
    fs.writeFileSync(path.join(directory, "request-start"), JSON.stringify({
      instanceId: instance.instanceId || "",
      requestedAtUnixMs: Date.now(),
      readOnly: true,
    }));
    return null;
  } catch (error) {
    return {
      code: "START_REQUEST_FAILED",
      error: `Could not request start for '${instance.label}': ${error.message}`,
      action: "Start the server from AI Unity MCP Server → Server → Start in that Editor.",
    };
  }
}

async function connectToUnity(target, timeoutSeconds) {
  const initial = resolveTarget({ target, requireServerOn: false });
  if (initial.error) return initial.error;

  selectedTarget = targetIdentity(initial.instance);
  let startRequested = false;
  const deadline = Date.now() + timeoutSeconds * 1000;
  let lastProbe = null;

  while (Date.now() <= deadline) {
    const current = resolveTarget({ requireServerOn: false });
    if (current.error) {
      if (["AMBIGUOUS", "INVALID_CONFIG"].includes(current.error.code)) return current.error;
    } else {
      selectedTarget = targetIdentity(current.instance);
      if (isReachableInstance(current.instance) || fixedPort()) {
        lastProbe = await probe(current.instance);
        if (lastProbe.reachable) {
          return {
            status: "connected",
            target: publicInstance(current.instance),
            source: current.source,
            writeGate: startRequested
              ? "OFF after remote start; enable it explicitly in Unity only when mutation is intended."
              : "Unchanged because the server was already online; inspect Unity before running mutation tools.",
          };
        }
      }

      if (!startRequested && !fixedPort() && !isReachableInstance(current.instance)) {
        const startError = writeStartRequest(current.instance);
        if (startError) return startError;
        startRequested = true;
      }
    }

    await sleep(400);
  }

  return {
    code: "CONNECT_TIMEOUT",
    error: `Unity did not become reachable within ${timeoutSeconds} seconds.`,
    action: "Wait for Unity compilation to finish, call unity_connection_status, then retry unity_connect.",
    target: selectedTarget,
    lastProbe,
  };
}

const server = new McpServer({
  name: "AIUnityMCPServer",
  version: "2.0.1",
}, {
  instructions: "Use unity_connection_status when connection state is unknown. Use unity_connect for one-call discovery, read-only start, selection, and health verification. If a result has code AMBIGUOUS, never guess: show the candidates and ask for an instanceId, pid, port, or full projectPath. Remote and automatic starts keep Allow Write Commands OFF; only the user should enable writes in Unity.",
});

server.tool("unity_list_instances", "List all discoverable Unity Editors, including AI Unity MCP Server state, stable instanceId, heartbeat freshness, project path, and the active target for this MCP session.", {}, async () => {
  const instances = listInstances();
  const active = resolveTarget({ requireServerOn: false });
  const activeIdentity = active.instance ? targetIdentity(active.instance) : null;
  const output = instances.map(instance => ({
    ...publicInstance(instance),
    active: !!activeIdentity && (
      (activeIdentity.instanceId && instance.instanceId === activeIdentity.instanceId)
      || (!activeIdentity.instanceId && instance.pid === activeIdentity.pid)
    ),
  }));
  if (output.length) return textResult(output);
  return textResult({
    instances: [],
    note: "No Unity Editor with AI Unity MCP Server is discoverable.",
    action: "Open Unity and wait for package compilation to finish.",
    debug: { searched: instanceDirs(), preferredProject: preferredProjectRoot(), cwd: process.cwd() },
  });
});

server.tool("unity_connection_status", "Diagnose Unity MCP discovery and connectivity without changing config or starting an Editor. Returns a structured error code and next action when offline or ambiguous.", {}, async () => {
  const instances = listInstances();
  const resolved = resolveTarget({ requireServerOn: false });
  if (resolved.error) {
    return textResult({
      status: resolved.error.code === "AMBIGUOUS" ? "ambiguous" : "offline",
      ...resolved.error,
      preferredProject: preferredProjectRoot(),
      discoveredCount: instances.length,
    });
  }

  const health = (isReachableInstance(resolved.instance) || fixedPort())
    ? await probe(resolved.instance)
    : { reachable: false, error: "presence reports server OFF or heartbeat stale" };
  return textResult({
    status: health.reachable ? "connected" : "offline",
    target: publicInstance(resolved.instance),
    source: resolved.source,
    preferredProject: preferredProjectRoot(),
    discoveredCount: instances.length,
    probe: health,
    action: health.reachable ? "Ready." : "Call unity_connect. Pass an exact target if more than one project is open.",
  });
});

server.tool("unity_connect", "One-call Unity MCP connection: deterministically discovers the target, starts its server read-only when needed, follows port changes, and verifies ping. Returns AMBIGUOUS instead of silently choosing across projects.", {
  target: z.string().optional().describe("Optional exact instanceId, pid, port, label, project name, or full projectPath"),
  timeoutSeconds: z.number().min(1).max(30).optional().default(DEFAULT_CONNECT_TIMEOUT_SECONDS),
}, async ({ target, timeoutSeconds }) => textResult(await connectToUnity(target, timeoutSeconds)));

server.tool("unity_select_instance", "Select which already-online Unity Editor subsequent unity_* commands target. Kept for backward compatibility; prefer unity_connect.", {
  target: z.string().describe("Exact instanceId, pid, port, label, project name, or full projectPath"),
}, async ({ target }) => {
  const resolved = resolveTarget({ target, requireServerOn: true });
  if (resolved.error) return textResult(resolved.error);
  selectedTarget = targetIdentity(resolved.instance);
  return textResult({ selected: publicInstance(resolved.instance), source: resolved.source });
});

server.tool("unity_start_instance", "Start and select a Unity Editor read-only. Kept for backward compatibility; equivalent to unity_connect with a target.", {
  target: z.string().describe("Exact instanceId, pid, port, label, project name, or full projectPath"),
}, async ({ target }) => textResult(await connectToUnity(target, DEFAULT_CONNECT_TIMEOUT_SECONDS)));

const commands = loadCommands();
for (const command of commands) {
  const options = { timeoutMs: command.timeoutMs, maxRetries: command.noRetry ? 1 : undefined };
  server.tool(command.tool, command.description, toZodShape(command.params), async args => {
    const response = await callUnity(command.path, args || {}, options);
    const screenshotPath = command.command === "capture_screenshot"
      ? response?.screenshot
      : command.command === "uitk_playtest" && response?.status === "done"
        ? response?.evidence?.screenshot
        : null;
    if (screenshotPath && !response.error) {
      try {
        const png = fs.readFileSync(screenshotPath);
        const metadata = command.command === "capture_screenshot"
          ? {
              screenshot: response.screenshot,
              view: response.view,
              mode: response.mode,
              size: response.size,
              bytes: response.bytes,
            }
          : response;
        return { content: [
          { type: "text", text: JSON.stringify(metadata, null, 2) },
          { type: "image", data: png.toString("base64"), mimeType: "image/png" },
        ] };
      } catch (error) {
        return textResult({ ...response, imageReadError: error.message });
      }
    }
    return textResult(response);
  });
}
console.error(`[AI Unity MCP Server] registered ${commands.length} Unity tools from manifest + 5 connection tools`);

const transport = new StdioServerTransport();
await server.connect(transport);
