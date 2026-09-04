import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = path.dirname(fileURLToPath(import.meta.url));
const packageRoot = path.resolve(scriptDirectory, "..", "..");
const expectedName = "com.villadiego.ai-mcp-unity-server";
const expectedServerName = "ai-mcp-unity-server";
const expectedDisplayName = "AI Unity MCP Server";
const repositoryBase = "https://github.com/villadiego2020/ai-mcp-unity-server";
const semverPattern = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$/;

function readJson(relativePath) {
  return JSON.parse(fs.readFileSync(path.join(packageRoot, relativePath), "utf8"));
}

function walk(directory) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    if ([".git", ".agent-memory", ".claude", "node_modules", "Library", "Temp", "Logs", "obj"].includes(entry.name)) continue;
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) files.push(...walk(fullPath));
    else if (entry.isFile()) files.push(fullPath);
  }
  return files;
}

function relative(file) {
  return path.relative(packageRoot, file).replaceAll("\\", "/");
}

function verifyManifest() {
  const root = readJson("package.json");
  const server = readJson("Server~/package.json");
  const lock = readJson("Server~/package-lock.json");

  assert.match(root.name, /^[a-z0-9]+(?:[.-][a-z0-9-]+){2,}$/);
  assert.equal(root.name, expectedName);
  assert.match(root.version, semverPattern);
  assert.equal(root.version, server.version);
  assert.equal(root.version, lock.version);
  assert.equal(root.version, lock.packages[""].version);
  assert.equal(server.name, expectedServerName);
  assert.equal(lock.name, expectedServerName);
  assert.equal(lock.packages[""].name, expectedServerName);
  assert.equal(server.private, true);
  assert.equal(root.displayName, expectedDisplayName);
  assert.equal(root.license, "MIT");
  assert.equal(root.author.name, "villadiego2020");
  assert.equal(root.repository.url, `${repositoryBase}.git`);
  assert.equal(root.homepage, `${repositoryBase}#readme`);
  assert.equal(root.bugs.url, `${repositoryBase}/issues`);
  for (const key of ["documentationUrl", "changelogUrl", "licensesUrl"]) {
    assert.ok(root[key].startsWith(repositoryBase), `${key} must target the canonical repository`);
  }
  assert.deepEqual(root.files, [
    "Editor",
    "Server~/commands.json",
    "Server~/index.js",
    "Server~/package.json",
    "Server~/package-lock.json",
    "Server~/registry.js",
    "Documentation~",
    "Tests",
    "README.md",
    "CHANGELOG.md",
    "LICENSE.md",
  ]);
  assert.equal(root.dependencies["com.unity.pipeline"], "0.6.0-exp.1");
  assert.equal(fs.readFileSync(path.join(packageRoot, "LICENSE.md"), "utf8").includes("Copyright (c) 2026 villadiego2020"), true);

  const requestedTag = process.argv.includes("--tag")
    ? process.argv[process.argv.indexOf("--tag") + 1]
    : process.env.RELEASE_TAG;
  if (requestedTag) assert.equal(requestedTag, `v${root.version}`, "release tag must exactly match package version");
}

function verifyCanonicalText() {
  const textExtensions = new Set([".cs", ".js", ".mjs", ".json", ".md", ".template", ".yml", ".yaml", ".ps1", ".asmdef"]);
  const textFiles = walk(packageRoot).filter(file => textExtensions.has(path.extname(file)) || path.basename(file) === ".gitignore");
  const legacyIdentityAllowed = new Set(["CHANGELOG.md", "Documentation~/migration-2.0.md", "Server~/test/branding.contract.test.js"]);
  const legacyCSharpIdentity = "MCP" + "Bridge";
  const legacyPackageIdentity = "com." + "mcp" + "bridge";
  const legacyIdentityPattern = new RegExp(`${legacyCSharpIdentity}|${legacyPackageIdentity.replace(".", "\\.")}`, "i");
  const legacyNamespacePattern = new RegExp(`namespace\\s+${legacyCSharpIdentity}\\b`);
  const prohibitedBrandPattern = new RegExp([
    "Delta" + "MCP",
    "Delta" + "[\\s_-]+AI",
    "Delta" + "-Project",
    "delta" + "-unity",
  ].join("|"), "i");
  const violations = [];

  for (const file of textFiles) {
    const name = relative(file);
    const content = fs.readFileSync(file, "utf8");
    if (/[\u0E00-\u0E7F]/u.test(content)) violations.push(`${name}: contains Thai text`);
    if (!legacyIdentityAllowed.has(name) && legacyIdentityPattern.test(content)) {
      violations.push(`${name}: contains a legacy technical identity`);
    }
    if (!name.endsWith("branding.contract.test.js") && prohibitedBrandPattern.test(content)) {
      violations.push(`${name}: contains a prohibited legacy brand`);
    }
  }
  assert.deepEqual(violations, []);

  const editorSources = walk(path.join(packageRoot, "Editor")).filter(file => file.endsWith(".cs"));
  for (const file of editorSources) {
    assert.equal(legacyNamespacePattern.test(fs.readFileSync(file, "utf8")), false, `${relative(file)} has the old namespace`);
  }
  assert.equal(readJson("Editor/AIUnityMCPServer.Editor.asmdef").name, "AIUnityMCPServer.Editor");
  assert.equal(readJson("Editor/TestRunner/AIUnityMCPServer.Editor.TestRunner.asmdef").name, "AIUnityMCPServer.Editor.TestRunner");
}

function verifyDocumentationLinks() {
  const markdownFiles = walk(packageRoot).filter(file => file.endsWith(".md"));
  const missing = [];
  for (const file of markdownFiles) {
    const content = fs.readFileSync(file, "utf8");
    for (const match of content.matchAll(/\[[^\]]+\]\((?!https?:|mailto:|#)([^)#]+)(?:#[^)]+)?\)/g)) {
      const target = path.resolve(path.dirname(file), decodeURIComponent(match[1]));
      if (!fs.existsSync(target)) missing.push(`${relative(file)} -> ${match[1]}`);
    }
  }
  assert.deepEqual(missing, []);
}

function verifyToolContract() {
  const manifest = readJson("Server~/commands.json");
  assert.equal(manifest.commands.length, 73);
  assert.equal(new Set(manifest.commands.map(command => command.tool)).size, 73);
  assert.equal(new Set(manifest.commands.map(command => command.path)).size, 73);
  const dispatcher = walk(path.join(packageRoot, "Editor"))
    .filter(file => /^MCPHandlers(?:\.[^.]+)?\.cs$/.test(path.basename(file)))
    .map(file => fs.readFileSync(file, "utf8"))
    .join("\n");
  assert.deepEqual(manifest.commands.filter(command => !dispatcher.includes(`"${command.path}"`)).map(command => command.path), []);
}

function verifyTarball() {
  const npmArguments = ["pack", "--dry-run", "--json", "--ignore-scripts", "."];
  const commandOptions = { cwd: packageRoot, encoding: "utf8" };
  const output = process.platform === "win32"
    ? execFileSync(
        process.env.ComSpec || "cmd.exe",
        ["/d", "/s", "/c", "npm pack --dry-run --json --ignore-scripts ."],
        commandOptions)
    : execFileSync("npm", npmArguments, commandOptions);
  const result = JSON.parse(output)[0];
  const names = result.files.map(file => file.path.replaceAll("\\", "/"));
  const allowed = /^(package\.json|README\.md|CHANGELOG\.md|LICENSE\.md|Editor\/|Server~\/|Documentation~\/|Tests\/)/;
  const forbidden = /(^|\/)(\.agent-memory|\.claude|\.github|Tools~|node_modules|Library|Temp|Logs|obj|cache)(\/|$)|(^|\/)(\.env|\.npmrc|id_rsa|.*\.(pem|pfx|key))$/i;
  assert.deepEqual(names.filter(name => !allowed.test(name)), []);
  assert.deepEqual(names.filter(name => forbidden.test(name)), []);
  for (const required of ["package.json", "LICENSE.md", "Server~/index.js", "Server~/commands.json", "Server~/package-lock.json", "Editor/AIUnityMCPServer.Editor.asmdef"]) {
    assert.ok(names.includes(required), `tarball is missing ${required}`);
  }
}

verifyManifest();
verifyCanonicalText();
verifyDocumentationLinks();
verifyToolContract();
verifyTarball();
console.log("Release verification passed for com.villadiego.ai-mcp-unity-server@2.0.0.");
