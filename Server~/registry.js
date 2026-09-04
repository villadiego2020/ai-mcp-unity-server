import fs from "fs";
import os from "os";
import path from "path";
import { fileURLToPath } from "url";

const BRIDGE_DIR = path.dirname(fileURLToPath(import.meta.url));
const SHARED_INSTANCES_DIR = path.join(os.homedir(), ".AIUnityMCPServer", "instances");
const UNITY_PROJECT_MARKERS = ["Assets", "ProjectSettings"];
const MAX_PARENT_WALK = 12;
const PRESENCE_SCHEMA_VERSION = 2;
const DEFAULT_HEARTBEAT_STALE_MS = 15000;

function heartbeatStaleMilliseconds() {
  const configured = Number(process.env.UNITY_MCP_HEARTBEAT_STALE_MS);
  return Number.isFinite(configured) && configured >= 1000
    ? configured
    : DEFAULT_HEARTBEAT_STALE_MS;
}

export function stripCloneSuffix(projectRoot) {
  const match = /^(.*)_clone_\d+$/.exec(path.basename(projectRoot));
  return match ? path.join(path.dirname(projectRoot), match[1]) : projectRoot;
}

function findUnityProjectRoot(startDirectory) {
  if (!startDirectory) return null;
  let directory = path.resolve(startDirectory);
  for (let depth = 0; depth < MAX_PARENT_WALK; depth++) {
    const isUnityProject = UNITY_PROJECT_MARKERS.every(marker => fs.existsSync(path.join(directory, marker)));
    if (isUnityProject) return directory;
    const parent = path.dirname(directory);
    if (parent === directory) break;
    directory = parent;
  }
  return null;
}

export function preferredProjectRoot() {
  const explicit = process.env.UNITY_PROJECT_PATH;
  if (explicit && fs.existsSync(explicit)) return path.resolve(explicit);
  return findUnityProjectRoot(BRIDGE_DIR) || findUnityProjectRoot(process.cwd());
}

export function instanceDirs() {
  const directories = [SHARED_INSTANCES_DIR];
  const root = preferredProjectRoot();
  if (root) directories.push(path.join(stripCloneSuffix(root), "Library", "AIUnityMCPServer", "instances"));
  return [...new Set(directories)];
}

function presenceFilesIn(directory) {
  try {
    if (!fs.existsSync(directory)) return [];
    return fs.readdirSync(directory)
      .filter(file => file.endsWith(".json"))
      .map(file => path.join(directory, file));
  } catch (error) {
    console.error(`[AI Unity MCP Server] Could not read registry ${directory}: ${error.message}`);
    return [];
  }
}

function readPresenceFile(file) {
  try {
    let text = fs.readFileSync(file, "utf8");
    if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
    const entry = JSON.parse(text);
    entry._presenceFile = file;
    entry._modifiedUnixMs = fs.statSync(file).mtimeMs;
    return entry;
  } catch (error) {
    console.error(`[AI Unity MCP Server] Could not read presence file ${file}: ${error.message}`);
    return null;
  }
}

function isProcessAlive(pid) {
  if (!pid) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

function normalizedPath(value) {
  if (!value) return "";
  const normalized = stripCloneSuffix(path.resolve(value)).replace(/[\\/]+$/, "");
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

function exactPath(value) {
  if (!value) return "";
  const normalized = path.resolve(value).replace(/[\\/]+$/, "");
  return process.platform === "win32" ? normalized.toLowerCase() : normalized;
}

export function isSameProject(left, right) {
  return !!left && !!right && normalizedPath(left) === normalizedPath(right);
}

function normalizeEntry(entry) {
  const schemaVersion = Number(entry.schemaVersion || 1);
  const heartbeatUnixMs = Number(entry.heartbeatUnixMs || entry._modifiedUnixMs || 0);
  const heartbeatAgeMs = Math.max(0, Date.now() - heartbeatUnixMs);
  return {
    ...entry,
    schemaVersion,
    instanceId: String(entry.instanceId || ""),
    pid: Number(entry.pid || 0),
    port: Number(entry.port || 0),
    serverOn: !!entry.serverOn,
    processKind: String(entry.processKind || "legacy"),
    heartbeatUnixMs,
    heartbeatAgeMs,
    stale: schemaVersion >= PRESENCE_SCHEMA_VERSION && heartbeatAgeMs > heartbeatStaleMilliseconds(),
  };
}

function chooseFreshest(left, right) {
  const score = entry => [
    entry.schemaVersion,
    entry.stale ? 0 : 1,
    entry.serverOn ? 1 : 0,
    entry.heartbeatUnixMs,
  ];
  const leftScore = score(left);
  const rightScore = score(right);
  for (let index = 0; index < leftScore.length; index++) {
    if (leftScore[index] !== rightScore[index]) return leftScore[index] > rightScore[index] ? left : right;
  }
  return left;
}

function instanceSlot(entry) {
  return `${normalizedPath(entry.projectPath)}|${String(entry.label || "").toLowerCase()}`;
}

export function listInstances() {
  const byIdentity = new Map();
  for (const directory of instanceDirs()) {
    for (const file of presenceFilesIn(directory)) {
      const raw = readPresenceFile(file);
      if (!raw) continue;
      const entry = normalizeEntry(raw);
      if (!isProcessAlive(entry.pid)) continue;
      if (entry.schemaVersion >= PRESENCE_SCHEMA_VERSION && entry.processKind !== "editor") continue;

      const identity = entry.instanceId ? `instance:${entry.instanceId}` : `pid:${entry.pid}`;
      const existing = byIdentity.get(identity);
      byIdentity.set(identity, existing ? chooseFreshest(existing, entry) : entry);
    }
  }

  const entries = [...byIdentity.values()];
  const modernSlots = new Set(entries.filter(entry => entry.instanceId).map(instanceSlot));
  const modern = entries.filter(entry => entry.instanceId);
  const legacyBySlot = new Map();
  for (const entry of entries.filter(candidate => !candidate.instanceId)) {
    const slot = instanceSlot(entry);
    if (modernSlots.has(slot)) continue;
    const existing = legacyBySlot.get(slot);
    legacyBySlot.set(slot, existing ? chooseFreshest(existing, entry) : entry);
  }

  return [...modern, ...legacyBySlot.values()].sort(compareInstances);
}

function compareInstances(left, right) {
  const projectOrder = normalizedPath(left.projectPath).localeCompare(normalizedPath(right.projectPath));
  if (projectOrder !== 0) return projectOrder;
  const labelOrder = String(left.label || "").localeCompare(String(right.label || ""), undefined, { numeric: true });
  if (labelOrder !== 0) return labelOrder;
  return left.pid - right.pid;
}

export function isReachableInstance(instance) {
  return !!instance && instance.serverOn && !instance.stale && Number.isInteger(instance.port) && instance.port > 0;
}

function matchesSelected(entry, selected) {
  if (!selected) return false;
  if (selected.instanceId) return entry.instanceId === selected.instanceId;
  if (selected.pid) return entry.pid === selected.pid;
  if (selected.port) return entry.port === selected.port;
  return !!selected.projectPath
    && exactPath(entry.projectPath) === exactPath(selected.projectPath)
    && String(entry.label || "").toLowerCase() === String(selected.label || "").toLowerCase();
}

export function findMatchingInstances(instances, target) {
  const requested = String(target || "").trim().toLowerCase();
  if (!requested) return [];
  return instances.filter(entry => {
    const values = [entry.instanceId, entry.pid, entry.port, entry.label, entry.project, entry.projectPath];
    if (values.some(value => String(value ?? "").toLowerCase() === requested)) return true;
    try {
      return !!entry.projectPath && exactPath(entry.projectPath) === exactPath(target);
    } catch {
      return false;
    }
  });
}

function candidateSummary(entry) {
  return {
    instanceId: entry.instanceId || undefined,
    label: entry.label,
    project: entry.project,
    projectPath: entry.projectPath,
    pid: entry.pid,
    port: entry.port,
    serverOn: entry.serverOn,
    stale: entry.stale,
  };
}

function ambiguity(candidates, message) {
  return {
    error: {
      code: "AMBIGUOUS",
      error: message,
      action: "Call unity_list_instances, then pass an instanceId, pid, port, or full projectPath to unity_connect.",
      candidates: candidates.map(candidateSummary),
    },
  };
}

function selectWithinProject(candidates) {
  if (candidates.length === 1) return { instance: candidates[0], source: "only-instance" };
  const main = candidates.filter(entry => String(entry.label || "").toLowerCase() === "main");
  if (main.length === 1) return { instance: main[0], source: "project-main" };
  return ambiguity(candidates, "Multiple Unity Editors match this project and no unique Main instance can be selected safely.");
}

export function resolveInstance(instances, options = {}) {
  const all = [...instances].sort(compareInstances);
  const target = String(options.target || "").trim();
  if (target) {
    const matches = findMatchingInstances(all, target);
    if (!matches.length) {
      return {
        error: {
          code: "NOT_FOUND",
          error: `No Unity Editor matches '${target}'.`,
          action: "Call unity_list_instances and use an exact instanceId, pid, port, or projectPath.",
          candidates: all.map(candidateSummary),
        },
      };
    }
    if (matches.length > 1) return ambiguity(matches, `Target '${target}' matches more than one Unity Editor.`);
    return validateReachability(matches[0], options.requireServerOn, "explicit-target");
  }

  if (options.selected) {
    const selectedMatches = all.filter(entry => matchesSelected(entry, options.selected));
    if (selectedMatches.length === 1) return validateReachability(selectedMatches[0], options.requireServerOn, "session-selection");
    if (selectedMatches.length > 1) return ambiguity(selectedMatches, "The selected Unity identity now matches more than one Editor.");
    return {
      error: {
        code: "SELECTED_NOT_FOUND",
        error: "The Unity Editor selected for this MCP session is no longer discoverable.",
        action: "Call unity_list_instances, then unity_connect with the intended target. The bridge will not switch projects silently.",
        candidates: all.map(candidateSummary),
      },
    };
  }

  if (!all.length) {
    return {
      error: {
        code: "NOT_FOUND",
        error: "No Unity Editor with AI Unity MCP Server is currently discoverable.",
        action: "Open a Unity project that contains AI Unity MCP Server and wait for compilation to finish.",
        candidates: [],
      },
    };
  }

  const preferredRoot = options.preferredRoot || preferredProjectRoot();
  const preferred = preferredRoot ? all.filter(entry => isSameProject(entry.projectPath, preferredRoot)) : [];
  if (preferred.length) {
    const resolved = selectWithinProject(preferred);
    return resolved.instance
      ? validateReachability(resolved.instance, options.requireServerOn, resolved.source)
      : resolved;
  }

  const projectGroups = new Set(all.map(entry => normalizedPath(entry.projectPath)));
  if (projectGroups.size > 1) {
    return ambiguity(all, "More than one Unity project is open and this MCP session is not scoped to one of them.");
  }

  const resolved = selectWithinProject(all);
  return resolved.instance
    ? validateReachability(resolved.instance, options.requireServerOn, resolved.source)
    : resolved;
}

function validateReachability(instance, requireServerOn, source) {
  if (requireServerOn && !isReachableInstance(instance)) {
    return {
      error: {
        code: "NOT_CONNECTED",
        error: `Unity Editor '${instance.label}' is present but its AI Unity MCP Server is offline or stale.`,
        action: "Call unity_connect to start or reconnect it without enabling write commands.",
        candidates: [candidateSummary(instance)],
      },
      instance,
      source,
    };
  }
  return { instance, source };
}

export function pickInstancePort(instances, selectedPort = null) {
  const resolved = resolveInstance(instances, {
    selected: selectedPort ? { port: selectedPort } : null,
    requireServerOn: true,
  });
  return resolved.instance && !resolved.error ? resolved.instance.port : null;
}
