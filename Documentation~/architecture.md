# AI Unity MCP Server — internals

Reference material for people who already have the package running, or who want to work on it.
Install, first run and daily use are in the [README](../README.md).

- [Request flow](#request-flow)
- [Components](#components)
- [How the bridge finds your Editor](#how-the-bridge-finds-your-editor)
- [Codex setup and Doctor](#codex-setup-and-doctor)
- [The `.mcp.json` file Unity writes for you](#the-mcpjson-file-unity-writes-for-you)
- [Write gate and retry rules](#write-gate-and-retry-rules)
- [Repo layout](#repo-layout)
- [Running the tests](#running-the-tests)

---

## Request flow

```
Codex / MCP client ──stdio──► Server~/index.js ──HTTP POST /path──► MCPServer (TcpListener)
                                          ▲                                      │
                                   commands.json                          MCPHandlers.Dispatch
                                  (single source)                     rate limit → write gate → route
                                          ▼                                      │
In-editor chat (F12) ──────────────────────────────────────────────────► main-thread execution
```

## Components

- **`Editor/MCPServer.cs`** — a small `TcpListener` HTTP server on a background thread, bound to
  `IPAddress.Loopback`, port **23457** (ParrelSync clones take 23458 and up; the search range is
  23457–23466). `TcpListener` rather than `HttpListener` on purpose: if Unity is force-quit, the kernel
  does not hold the port hostage until reboot. Asset Import Workers and batch-mode processes do not
  initialize the listener or publish presence.
- **`Editor/MCPSetup.cs`** — idempotent Codex registration and a read-only Doctor. It delegates config
  writes to `codex mcp`; it never edits Codex TOML directly.
- **`Editor/MCPRuntimeCache.cs`** — content-addressed per-user bootstrap for the Node bridge. It stages
  and validates package files before an atomic directory promotion, while npm dependencies remain
  outside read-only Package Manager locations.
- **`Editor/MCPPackagePaths.cs`** — the shared Package Manager-aware resolver for `Server~/index.js`
  and `Server~/commands.json`, so transport and dispatcher cannot drift to different manifests.
- **`Editor/MCPHandlers*.cs`** — the dispatcher and the handlers, split by pack (core, assist, edit,
  offline). Work that touches Unity APIs is marshalled to the main thread.
- **`Server~/commands.json`** — the single source of truth for tool name, route, description and
  parameter schema. The Node bridge turns it into MCP tools with Zod schemas; the C# side reads it to
  map command names to routes. Add a tool in one place and both sides see it.
- **`Server~/registry.js`** — Editor discovery (below).
- **The in-editor chat** calls `MCPHandlers.Dispatch` directly, in-process — it never goes over HTTP.

External tool names are the command names prefixed with `unity_` (`read_console` →
`unity_read_console`). Three are spelled differently: `set_terrain_heights` →
`unity_terrain_set_heights`, `diagnose_deep` → `unity_deep_analysis`, `get_exceptions` →
`unity_exceptions`.

## How the bridge finds your Editor

Every open interactive Editor writes an atomic presence file to two registries —
`<UnityProject>/Library/AIUnityMCPServer/instances/` and the machine-wide
`~/.AIUnityMCPServer/instances/`. Schema v2
includes `instanceId`, PID, process kind, project path, port, server state, package version and a
heartbeat. `instanceId` is stable for that Main/Clone project path, so selection survives domain reload
and port rebinding. A stale heartbeat is never treated as an online server.

That machine-wide registry is what makes discovery work no matter where the package sits on disk: Node
has no Package Manager API to ask, so with a `file:` reference or a separate bridge clone there is no
way to derive the Unity project from `index.js`'s own location.

When more than one Editor is running, the bridge prefers one belonging to *your* project — from
`UNITY_PROJECT_PATH` if set, else the Unity project that contains the bridge, else the project that
contains the client's working directory — then a unique `Main`. It never falls through to another
project or an arbitrary clone. A duplicate label or an unscoped multi-project session returns
`AMBIGUOUS` with exact candidates.

`unity_connection_status` reports discovery and a short ping without changing state. `unity_connect`
does the normal connection flow in one call: select deterministically, write the read-only start
request when the server is off, re-read presence while waiting, follow a changed port, and verify
`/ping`. `unity_list_instances`, `unity_select_instance` and `unity_start_instance` remain as backward-
compatible lower-level tools. Every retry for a normal manifest command resolves the target again;
non-idempotent commands still opt out of retries.

The registry reader merges duplicate schema-v2 files by stable ID, suppresses legacy PID entries when
a modern entry owns the same project/label slot, rejects non-Editor process kinds, ignores dead PIDs,
and exposes heartbeat staleness in `unity_list_instances`.

Two optional environment variables override all of that:

| Variable | Effect |
|---|---|
| `UNITY_PROJECT_PATH` | Pin discovery to one Unity project when several are open at once |
| `UNITY_MCP_PORT` | Skip discovery entirely and talk to a fixed port (e.g. `23457`) |

## Codex setup and Doctor

For Git, registry, local and embedded installs, **AI Unity MCP Server → Setup → Configure Codex** performs the
one-time machine setup:

1. validates Node, Codex and the required package bridge files;
2. fingerprints `index.js`, `registry.js`, `commands.json` and npm metadata;
3. stages and atomically promotes them to
   `<LocalApplicationData>/AIUnityMCPServer/runtime/<package-version>-<content-hash>`;
4. installs npm dependencies in that cache with the package lock when the MCP SDK is missing;
5. inspects `codex mcp get AIUnityMCPServer --json`;
6. returns `already configured`, registers the stable cached entry with `codex mcp add`, or stops without
   changing anything when the `AIUnityMCPServer` key points somewhere else.

Replacing a different `AIUnityMCPServer` registration requires the explicit **Repair Codex Registration** menu
item. Setup and Repair call the CLI rather than parsing or rewriting `~/.codex/config.toml`. **Doctor**
is read-only and reports package-source, runtime-bootstrap and dependency state separately. A second
Setup validates and reuses the cache without copying or reinstalling anything. A new Codex session is
required only after the first registration so the client can discover the newly available tools.

Runtime cache identities include both package version and a source-content hash. Updated builds can
coexist with an older cache while existing client processes finish; Setup registers only the current
identity. Invalid cache directories are moved aside before promotion rather than overwritten. npm
installation uses the cached lockfile with lifecycle scripts disabled and a per-cache process lock.
Configure can replace an older registration only when its entry is provably inside this managed cache;
an arbitrary `AIUnityMCPServer` registration still requires Repair and confirmation. This is bridge bootstrapping,
not Node runtime distribution: Node 18+ remains a prerequisite.

## The `.mcp.json` file Unity writes for you

For legacy project-scoped MCP clients, editable installs still write `<UnityProject>/.mcp.json` on
load with `args` pointing at this package's `Server~/index.js`. A Git/registry install writes it after
Setup has prepared the stable per-user cache, pointing at that cached `index.js` instead of
`Library/PackageCache`.
[`Documentation~/.mcp.json.template`](.mcp.json.template) shows the shape if you would rather write it
by hand.

The file is only written when it is **missing**, or when it contains **nothing but this package's own
entry** (which is how a stale path from an older version gets repaired). A `.mcp.json` you have
customised — other MCP servers, extra keys — is never overwritten; if its `AIUnityMCPServer` entry points at a
file that does not exist, you get one Console warning per session naming the path to use instead.
Git/registry installs leave the file alone until a valid runtime cache exists.

Codex does not use this file. Its global registration is managed only through `codex mcp`, which is
why a customised `.mcp.json` is always untouched and a customised Codex registration remains
untouched unless the user explicitly runs Repair.

With a local `file:` reference that absolute path is machine-specific, so gitignore `.mcp.json` if your
teammates keep their clone somewhere else — otherwise each of them will re-point it and commit the
churn.

## Write gate and retry rules

- Mutating routes are listed explicitly in `MCPHandlers.WritePaths` and are refused with an
  explanatory error until **AI Unity MCP Server → Allow Write Commands** is on. The gate applies to the
  in-editor chat and to external MCP clients alike — including every sub-command of `run_batch`.
- The dispatcher is rate limited to **25 commands per second**.
- `delete_asset` moves files to the OS trash and refuses folders, third-party paths and anything
  outside `Assets/`.
- `run_csharp`, `edit_script`, `run_batch`, `delete_asset`, `set_import_settings`, `build_player` and
  `run_tests` are flagged `noRetry`, so a timeout cannot silently re-run side effects.
- `unity_connect` and **Auto Start Read-Only** reset `AIUnityMCPServer_AllowWrites` to false before opening the
  listener. A remote connection can never inherit a write-enabled state from an earlier session.

## Repo layout

| Path | What it is |
|---|---|
| `Editor/` | The `MCPBridge.Editor` assembly — chat window, server, Setup/Doctor, package path resolver, handlers, profiler readers, code/prefab indexers, refactor audit, runtime watch, exception tracker |
| `Editor/TestRunner/` | Optional assembly; only compiles when `com.unity.test-framework` is present |
| `Editor/Fonts/` | Bundled IBM Plex Sans Thai Looped (OFL), so the UI renders the same everywhere |
| `Server~/` | Node stdio MCP bridge (`index.js`), Editor discovery (`registry.js`) and the `commands.json` manifest |
| `Tests/Editor/` | EditMode tests (batch parser, edit-script primitives, `.mcp.json` path rules), gated by `UNITY_INCLUDE_TESTS` |
| `Documentation~/` | This file, the `.mcp.json` template, and the Play Mode runtime-inspection guide |

## Running the tests

Add `"com.mcpbridge"` to `testables` in the consuming project's `Packages/manifest.json`, then use
**Window → General → Test Runner** (or the `run_tests` command).
