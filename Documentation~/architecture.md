# Architecture

AI Unity MCP Server is an Editor-only Unity 6 package with one command owner and three external entry
paths.

## Request paths

```text
MCP client -> Node stdio server -> loopback HTTP -> MCPHandlers.Dispatch
Native client -> Unity relay/Named Pipe -> optional Native adapter -> MCPHandlers.Dispatch
Unity CLI -> Unity Pipeline adapter -------------> MCPHandlers.Dispatch
In-Editor chat ----------------------------------> MCPHandlers.Dispatch
```

`Server~/commands.json` is the source of truth for all 73 shared tools. `Server~/index.js`
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
| `Editor/NativeMcp/` | Optional Assistant 2.18.x adapter; no hard Assistant dependency |
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
- Auto-start, `unity_connect` and explicit Native read-only start reset only this Editor's write gate to off.
- Destructive asset operations are path-bounded and move files to trash where supported.
- Manifest routes marked `noRetry` are sent once by the Node client. Other legacy routes may retry a
  connection failure, so a timed-out write must be treated as having an unknown outcome and inspected
  before manual retry.
- The Pipeline adapter adds no cancellation or deduplication protocol. The same timeout rule applies.

## Native lifecycle and contract

The optional assembly references Unity's public `IUnityMcpTool` and registry APIs only. Package
version defines and a matching source guard exclude it when the supported Assistant API is absent.
`MCPCommandCatalog` reads the same manifest as Node, producing schemas, applying defaults, stripping
unknown keys and validating values before one dispatcher invocation.

Registration runs after Editor initialization and after the registry's **Refreshed** event. Added,
removed and availability events do not trigger recursive registration. Explicit disables stay in
Unity's settings. Existing names are skipped and reported. Cleanup checks the public schema object
identity before unregistering a tool, so a later third-party replacement remains untouched.
Subscriptions and owned registrations are disposed before assembly reload and Editor shutdown.

Native results preserve dispatcher JSON in `data`, which Unity copies into `structuredContent`.
Dispatcher errors become native execution errors. The tested Unity relay drops image content blocks,
so screenshots include `data.imageDelivery` with a local path, verified PNG signature and byte count.
The client can open that local image, or use Node/TCP for inline MCP images. No unused base64 payload
is sent through Native. Native adds no Node worker and needs no TCP
listener. Native server-stop tools affect the TCP listener only, as described in their manifest.

Activity source is passed as a request argument, not a global mutable transport flag. Existing nested
batch calls keep their own Editor entries; the outer request records its actual transport.
Write permission uses Editor `SessionState` plus a thread-visible cache. It survives domain reload
but defaults OFF in a fresh Editor; legacy global write permission is intentionally not imported.

The optional assembly is a real dependency and lifecycle boundary: placing its references in core
would force Assistant on Node/CLI users. Remove the adapter when the supported Native API is retired.
The shared catalog avoids separate Node/Native parameter definitions; it can be removed if a single
upstream schema provider replaces the manifest for every transport. Connection hooks allow the core
window to query the optional assembly without reflection or a reverse assembly reference.

## Verification

Run the Node suite, focused Unity tests and release verifier for the installed version. Native tests
need a host with the supported Assistant API; absent-package compilation must also pass. Do not treat
static compilation as evidence that a client completed an MCP handshake. See the current task's test
report for actual host and relay results.

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
