# AI Unity MCP Server

![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-222222)
![Version 2.0.0](https://img.shields.io/badge/version-2.0.0-2d6cdf)
![73 Editor tools](https://img.shields.io/badge/tools-73-2d6cdf)
![MIT](https://img.shields.io/badge/license-MIT-green)

AI Unity MCP Server gives AI clients and the in-Editor chat a shared, evidence-based way to inspect,
test, and safely change a Unity 6 project. All Unity work goes through one C# dispatcher and one
explicit write gate; the package is Editor-only and is not included in player builds.

## Requirements

- Unity 6000.0 or newer
- Node.js 18 or newer for external MCP clients
- Codex CLI only when using automatic Codex registration
- `com.unity.pipeline` 0.6.0-exp.1, resolved as a package dependency

Photon Fusion tools activate when Fusion is installed and otherwise return an availability message.

## Install version 2.0.0

Open `Packages/manifest.json`, add the OpenUPM registry, and pin the package version:

```json
{
  "scopedRegistries": [
    {
      "name": "package.openupm.com",
      "url": "https://package.openupm.com",
      "scopes": ["com.villadiego.ai-mcp-unity-server"]
    }
  ],
  "dependencies": {
    "com.villadiego.ai-mcp-unity-server": "2.0.0"
  }
}
```

OpenUPM installation becomes available after the package metadata is accepted and a `v2.0.0` tag is
published. The same published version can be pinned directly to its immutable Git tag:

```json
"com.villadiego.ai-mcp-unity-server": "https://github.com/villadiego2020/ai-mcp-unity-server.git#v2.0.0"
```

For local package development, use Unity Package Manager's **Add package from disk** and select this
repository's `package.json`.

Upgrading from 1.0.0 is a breaking package identity change. Follow the
[migration and rollback guide](Documentation~/migration-2.0.md) instead of keeping both identities in
the same manifest.

## First run

1. Let Unity finish resolving packages and compiling.
2. Run **AI Unity MCP Server → Setup → Configure Codex**.
3. Run **AI Unity MCP Server → Setup → Doctor** and resolve every failed check.
4. Start a new Codex session, then call `unity_connection_status` followed by `unity_connect`.

Setup copies only the Node runtime files into a versioned, content-addressed directory under the
current user's local application data. This avoids writing npm dependencies into Unity's immutable
`PackageCache`. A valid cache and matching Codex registration are reused on later projects and
sessions, so setup does not need to be regenerated each time.

Other MCP clients can use the shape in [`Documentation~/.mcp.json.template`](Documentation~/.mcp.json.template).
For registry and Git installs, prefer Setup's stable cached `index.js` path over a `PackageCache` path.

## Use it

- Open the in-Editor chat with **AI Unity MCP Server → Chat** or `F12`.
- Use `unity_list_instances` when several Editors are open, then select the intended instance.
- Use `UNITY_PROJECT_PATH` to pin external discovery to one exact project.
- Inspect first. Enable **AI Unity MCP Server → Allow Write Commands** only for intended changes.
- Turn the write gate off after the change. Read-only auto-start also resets it to off.

The command manifest currently exposes exactly 73 MCP tools:

| Area | Tools | Examples |
|---|---:|---|
| Connection and compilation | 4 | ping, compile, compile status, server stop |
| Scene and content | 25 | hierarchy, objects, transforms, prefabs, terrain, materials |
| Assets, code, and build | 15 | audits, script edits, batch, build, Git status |
| Runtime diagnostics | 23 | Console, watches, events, screenshots, memory, performance |
| Test Runner | 2 | start tests, read results |
| UI Toolkit | 4 | inspect, validate, optimistic apply, semantic playtest |

`Server~/commands.json` is the authoritative command and parameter catalogue. The UI Toolkit workflow
is described in [UI Toolkit tools](Documentation~/ui-toolkit.md), and Play Mode diagnostics are in
[runtime inspection](Documentation~/runtime-inspection.md).

## Safety model

- The server binds to loopback only and starts read-only.
- Mutating routes are explicitly classified and rejected while the write gate is off.
- Asset deletion is restricted to project `Assets/` files and uses the operating-system trash.
- UI Toolkit writes use preview hashes, optimistic concurrency checks, backups, and rollback.
- Arbitrary C# and script-edit operations are available only behind the write gate.
- A timed-out write has an unknown outcome. Inspect project state before manually retrying; the legacy
  Node transport does not provide distributed cancellation or universal write deduplication.

## Unity CLI and official Unity MCP coexistence

Version 2.0.0 includes a thin Unity Pipeline adapter. `ai_mcp_list_commands` exposes the current
dispatcher routes and write state; `ai_mcp_dispatch` forwards one command and JSON body through
`MCPHandlers.Dispatch`, including the same rate limit and write gate. It does not duplicate handlers
or replace the Node transport.

This allows the official Unity CLI and its MCP server to run side-by-side with AI Unity MCP Server.
Static verification passed against Unity 6000.0.75f1 and `com.unity.pipeline` 0.6.0-exp.1, all 73
routes were checked for dispatcher parity, and the Node suite passed 17/17. Live Editor discovery was
not verified on the development machine because Unity Package Manager aborted with
`The "path" argument must be of type string. Received undefined`.

Treat the two servers as separate tool providers:

- Use the official Unity MCP server for its native Pipeline commands.
- Use AI Unity MCP Server for its 73-route manifest, multi-Editor discovery, Setup/Doctor, UI Toolkit
  workflow, and existing Node-compatible MCP clients.
- Always provide the exact project path in multi-Editor workflows. A timeout after dispatching a write
  requires state inspection before retrying.

See [architecture](Documentation~/architecture.md) for the request paths and integration boundary.

## Troubleshooting

- **Tools are missing in a new session:** run Configure Codex, confirm Doctor passes, then start a new
  client session so it reloads the tool list.
- **Connection is ambiguous:** set `UNITY_PROJECT_PATH` or select an instance explicitly.
- **A registry/Git install points into PackageCache:** rerun Configure Codex to register the managed
  runtime cache.
- **A custom `.mcp.json` was not changed:** this is intentional; Setup does not overwrite unrelated
  MCP server entries.
- **Pipeline command discovery fails:** verify the exact Unity project path and installed Pipeline
  version; the Node transport remains available independently.

## Documentation

- [Architecture and contributor checks](Documentation~/architecture.md)
- [UI Toolkit tools](Documentation~/ui-toolkit.md)
- [Runtime inspection](Documentation~/runtime-inspection.md)
- [2.0 migration and rollback](Documentation~/migration-2.0.md)
- [Changelog](CHANGELOG.md)
- [MIT license](LICENSE.md)

The bundled IBM Plex Sans Thai Looped font is licensed separately under the SIL Open Font License 1.1
in `Editor/Fonts/OFL.txt`.
