import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

import {
  findMatchingInstances,
  isReachableInstance,
  isSameProject,
  resolveInstance,
  stripCloneSuffix,
} from "../registry.js";

const registryModuleUrl = pathToFileURL(path.resolve("Server~/registry.js")).href;

function instance(overrides = {}) {
  return {
    schemaVersion: 2,
    instanceId: "unity-a",
    pid: 101,
    processKind: "editor",
    label: "Main",
    project: "ProjectA",
    projectPath: path.join(os.tmpdir(), "ProjectA"),
    port: 23457,
    serverOn: true,
    stale: false,
    ...overrides,
  };
}

test("v2 presence wins over legacy duplicate and import workers are excluded", () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "mcp-registry-"));
  const home = path.join(root, "home");
  const project = path.join(root, "Game");
  fs.mkdirSync(path.join(project, "Assets"), { recursive: true });
  fs.mkdirSync(path.join(project, "ProjectSettings"), { recursive: true });

  const script = String.raw`
    import fs from "node:fs";
    import os from "node:os";
    import path from "node:path";

    const project = process.env.TEST_PROJECT;
    const shared = path.join(os.homedir(), ".AIUnityMCPServer", "instances");
    const local = path.join(project, "Library", "AIUnityMCPServer", "instances");
    fs.mkdirSync(shared, { recursive: true });
    fs.mkdirSync(local, { recursive: true });
    const now = Date.now();
    const write = (directory, name, value) => fs.writeFileSync(path.join(directory, name), JSON.stringify(value));

    write(shared, "modern.json", { schemaVersion: 2, instanceId: "unity-game-main", pid: process.pid,
      processKind: "editor", label: "Main", project: "Game", projectPath: project,
      port: 23001, serverOn: true, heartbeatUnixMs: now - 100 });
    write(local, "modern.json", { schemaVersion: 2, instanceId: "unity-game-main", pid: process.pid,
      processKind: "editor", label: "Main", project: "Game", projectPath: project,
      port: 23002, serverOn: true, heartbeatUnixMs: now });
    write(shared, "legacy-duplicate.json", { pid: process.pid, label: "Main", project: "Game",
      projectPath: project, port: 23003, serverOn: true });
    write(shared, "legacy-clone.json", { pid: process.pid, label: "Clone 0", project: "Game_clone_0",
      projectPath: project + "_clone_0", port: 23004, serverOn: true });
    write(shared, "worker.json", { schemaVersion: 2, instanceId: "unity-worker", pid: process.pid,
      processKind: "asset-import-worker", label: "Main", project: "Game", projectPath: project,
      port: 23005, serverOn: true, heartbeatUnixMs: now });

    const registry = await import(process.env.REGISTRY_MODULE_URL);
    console.log(JSON.stringify(registry.listInstances()));
  `;

  try {
    const output = execFileSync(process.execPath, ["--input-type=module", "--eval", script], {
      cwd: root,
      encoding: "utf8",
      env: {
        ...process.env,
        HOME: home,
        USERPROFILE: home,
        UNITY_PROJECT_PATH: project,
        TEST_PROJECT: project,
        REGISTRY_MODULE_URL: registryModuleUrl,
      },
    });
    const entries = JSON.parse(output);
    assert.equal(entries.length, 2);
    assert.equal(entries.find(entry => entry.instanceId === "unity-game-main").port, 23002);
    assert.equal(entries.some(entry => entry.processKind === "asset-import-worker"), false);
    assert.equal(entries.filter(entry => entry.label === "Main").length, 1);
    assert.equal(entries.find(entry => entry.label === "Clone 0").schemaVersion, 1);
  } finally {
    fs.rmSync(root, { recursive: true, force: true });
  }
});

test("clone paths belong to the same project while retaining exact instance identity", () => {
  const main = path.join(os.tmpdir(), "Game");
  const clone = `${main}_clone_2`;
  assert.equal(stripCloneSuffix(clone), main);
  assert.equal(isSameProject(main, clone), true);
});

test("duplicate labels across projects are ambiguous and never silently selected", () => {
  const projectA = path.join(os.tmpdir(), "McpProjectA");
  const projectB = path.join(os.tmpdir(), "McpProjectB");
  const instances = [
    instance({ instanceId: "unity-a", projectPath: projectA }),
    instance({ instanceId: "unity-b", project: "ProjectB", projectPath: projectB, port: 23458 }),
  ];

  assert.equal(resolveInstance(instances, { target: "Main" }).error.code, "AMBIGUOUS");
  assert.equal(resolveInstance(instances, { preferredRoot: path.join(os.tmpdir(), "Unrelated") }).error.code, "AMBIGUOUS");

  const scoped = resolveInstance(instances, { preferredRoot: projectA });
  assert.equal(scoped.instance.instanceId, "unity-a");
});

test("a lost session selection does not fail over to another project", () => {
  const onlyOtherProject = instance({ instanceId: "unity-b", project: "ProjectB" });
  const resolved = resolveInstance([onlyOtherProject], {
    selected: { instanceId: "unity-a" },
    requireServerOn: true,
  });
  assert.equal(resolved.error.code, "SELECTED_NOT_FOUND");
});

test("stable instance selection follows a changed port", () => {
  const rebound = instance({ instanceId: "unity-a", port: 24567 });
  const resolved = resolveInstance([rebound], {
    selected: { instanceId: "unity-a", port: 23457 },
    requireServerOn: true,
  });
  assert.equal(resolved.instance.port, 24567);
});

test("stale or offline presence is not reachable", () => {
  const stale = instance({ stale: true });
  const offline = instance({ serverOn: false });
  assert.equal(isReachableInstance(stale), false);
  assert.equal(isReachableInstance(offline), false);
  assert.equal(resolveInstance([stale], { requireServerOn: true }).error.code, "NOT_CONNECTED");
});

test("explicit selectors require exact matches", () => {
  const editor = instance();
  assert.equal(findMatchingInstances([editor], "unity-a").length, 1);
  assert.equal(findMatchingInstances([editor], String(editor.pid)).length, 1);
  assert.equal(findMatchingInstances([editor], "Project").length, 0);
});
