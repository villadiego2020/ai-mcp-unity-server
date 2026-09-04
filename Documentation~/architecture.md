# Architecture

AI Unity MCP Server is an Editor-only Unity 6 package with one command owner and two external entry
paths.

## Request paths

```text
MCP client -> Node stdio server -> loopback HTTP -> MCPHandlers.Dispatch
Unity CLI -> Unity Pipeline adapter -------------> MCPHandlers.Dispatch
In-Editor chat ----------------------------------> MCPHandlers.Dispatch
```

`Server~/commands.json` is the source of truth for all 73 Node-exposed tools. `Server~/index.js`
converts the manifest to MCP tools, resolves the intended Editor instance, and sends an HTTP request.
`MCPServer` accepts loopback requests and marshals Unity API work onto the Editor main thread.

The Pipeline adapter in `Editor/MCPHandlers.Pipeline.cs` publishes `ai_mcp_list_commands` and
`ai_mcp_dispatch`. It delegates directly to the same dispatcher, so it inherits routing, rate limiting,
and write authorization. It intentionally does not replace the Node server or implement another
command catalogue.

## Package components

| Path | Responsibility |
|---|---|
| `Editor/` | `AIUnityMCPServer.Editor`: UI, setup, server, discovery, handlers, diagnostics, and Pipeline adapter |
| `Editor/TestRunner/` | Optional `AIUnityMCPServer.Editor.TestRunner` integration |
| `Server~/commands.json` | Public tool names, routes, schemas, timeouts, and retry declarations |
| `Server~/index.js` | MCP stdio transport and Editor request client |
| `Server~/registry.js` | Multi-Editor presence discovery and deterministic target selection |
| `Documentation~/` | User and contributor references |
| `Tests/` and `Server~/test/` | Unity static/EditMode and Node contract coverage |

## Discovery and targeting

Each interactive Editor publishes schema-v2 presence into the project Library and the current user's
machine registry. Entries include a stable instance ID, project path, process ID, port, state,
package version, and heartbeat. Asset import workers and batch-mode processes do not publish.

Selection is scoped to `UNITY_PROJECT_PATH`, the package's Unity project, or the client's working
directory. An ambiguous selection returns candidates instead of choosing another project or clone.
`UNITY_MCP_PORT` can explicitly bypass discovery for diagnostics.

## Setup and immutable packages

Package Manager caches are treated as read-only. Setup fingerprints the Node runtime files and copies
them to `<LocalApplicationData>/AIUnityMCPServer/runtime/<version>-<hash>`, then runs lockfile-based npm
installation there with lifecycle scripts disabled. It uses `codex mcp` to inspect and register that
stable path; it does not edit Codex configuration files directly.

Setup reuses a valid cache and registration. Repair replaces only a user-confirmed conflicting
registration. Doctor performs read-only checks for package files, cache integrity, Node dependencies,
Codex registration, and Editor availability.

## Safety and delivery semantics

- The HTTP server binds only to loopback and limits dispatch rate.
- Mutating routes are listed explicitly and require the Editor write gate.
- Auto-start and `unity_connect` reset the write gate to off.
- Destructive asset operations are path-bounded and move files to trash where supported.
- Manifest routes marked `noRetry` are sent once by the Node client. Other legacy routes may retry a
  connection failure, so a timed-out write must be treated as having an unknown outcome and inspected
  before manual retry.
- The Pipeline adapter adds no cancellation or deduplication protocol. The same timeout rule applies.

## Verification status for 2.0.1

- Node integration and contract suite: 23/23 passing.
- Manifest-to-dispatch parity: all 73 routes checked.
- Static production/test compilation: Unity 6000.0.75f1 with `com.unity.pipeline` 0.6.0-exp.1.
- Live Pipeline discovery: not verified because the local Package Manager aborted with
  `The "path" argument must be of type string. Received undefined`.

The Node and Pipeline paths therefore remain side-by-side. Exact project-path targeting is required
for multi-Editor use.

## Contributor checks

Every published change must increment the package version and add a dated release section to
`CHANGELOG.md`. Do not accumulate changes under an `Unreleased` section.

Install Node dependencies without scripts, run Node tests, then run the release verifier:

```powershell
npm ci --prefix Server~ --ignore-scripts
node --test Server~/test/*.test.js
node Tools~/Release/verify-release.mjs
```

To include Unity package tests in a consuming project, add
`com.villadiego.ai-mcp-unity-server` to `testables` and run the Unity Test Runner. The static compiler
helper in `Tests/StaticCompile.ps1` can validate production and test sources against an existing Unity
6 project and its resolved package sources.
