# AI Unity MCP Server

![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-222222)
![Version 2.1.0](https://img.shields.io/badge/version-2.1.0-2d6cdf)
![73 Editor tools](https://img.shields.io/badge/tools-73-2d6cdf)
![MIT](https://img.shields.io/badge/license-MIT-green)

AI Unity MCP Server gives AI clients and the in-Editor chat a shared, evidence-based way to inspect,
test, and safely change a Unity 6 project. All Unity work goes through one C# dispatcher and one
explicit write gate; the package is Editor-only and is not included in player builds.

## Requirements

- Unity 6000.0 or newer
- Node.js 18 or newer only for the Node/TCP transport
- Optional Unity AI Assistant 2.18.0-pre.2 through 2.18.x for Native MCP compatibility
- Codex CLI only when using automatic Codex registration
- `com.unity.pipeline` 0.6.0-exp.1, resolved as a package dependency

Photon Fusion tools activate when Fusion is installed and otherwise return an availability message.

## Install version 2.1.0

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
    "com.villadiego.ai-mcp-unity-server": "2.1.0"
  }
}
```

OpenUPM installation becomes available after the package metadata is accepted and a `v2.1.0` tag is
published. The same published version can be pinned directly to its immutable Git tag:

```json
"com.villadiego.ai-mcp-unity-server": "https://github.com/villadiego2020/ai-mcp-unity-server.git#v2.1.0"
```

For local package development, use Unity Package Manager's **Add package from disk** and select this
repository's `package.json`.

Upgrading from 1.0.0 is a breaking package identity change. Follow the
[migration and rollback guide](Documentation~/migration-2.0.md) instead of keeping both identities in
the same manifest.

## First run (Node/TCP)

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

- The TCP server binds to loopback only and starts read-only.
- Our write permission belongs to this Editor session, survives domain reload, and starts OFF in a new Editor.
  A read-only connection to another Editor cannot change this session.
- Mutating routes are explicitly classified and rejected while the write gate is off.
- Asset deletion is restricted to project `Assets/` files and uses the operating-system trash.
- UI Toolkit writes use preview hashes, optimistic concurrency checks, backups, and rollback.
- Arbitrary C# and script-edit operations are available only behind the write gate.
- A timed-out write has an unknown outcome. Inspect project state before manually retrying; the legacy
  Node transport does not provide distributed cancellation or universal write deduplication.

## One toolset, three connections

Open **AI Unity MCP Server → Connections** to see the exact project, Editor identity, write state,
Native tool counts and connected clients. Choose the connection your client supports:

| Connection | Setup | Shared tools |
|---|---|---|
| Native Unity MCP | Install supported Unity AI Assistant, open Connections, start Native with our writes OFF, then use Native Settings to configure your client | All 73 individual tools with full schemas; no additional Node process |
| Node/TCP | Configure Codex and run Doctor using the first-run steps above | All 73 tools plus 5 multi-Editor connection tools |
| Unity CLI / MCP | Target this project with Unity CLI and use its MCP setup | `ai_mcp_list_commands` returns all schemas; `ai_mcp_dispatch` executes a command |

The optional Native adapter adds the **AI Unity MCP Server** tool group automatically. Existing group
and per-tool disables are preserved. It shares the dispatcher, rate limit, per-Editor write gate and
Activity log with Node and Pipeline. Native tool-name collisions are reported without replacing other
tools. Registry refresh and domain reload restore our tools without duplicates.

Unity's built-in Native tools keep their own permissions and activity logs. Our write switch does not
govern those tools. Native client selection must use the exact project shown in Connections.
Native screenshots return verified local PNG paths for the client to open; the tested Unity relay
does not forward inline image blocks. Use Node/TCP when the client needs inline MCP images.

Unity has deprecated Native MCP in its Assistant documentation in favor of Unity CLI. This adapter
supports the installed Native API as a compatibility path; Node and Pipeline remain available if a
future Assistant version removes that API. See [Native setup](Documentation~/native-mcp.md) and
[architecture](Documentation~/architecture.md) for the supported boundary.

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
- **Activity is empty:** only requests reaching our dispatcher appear here. Built-in Unity tools
  have their own logs. Check Connections for the project, Native enabled-tool count and connected clients.
- **Native tools are missing:** check the AI Unity MCP Server group and per-tool switches in Native
  Settings. Verify the supported Assistant version; Node and CLI are available independently.

## Documentation

- [Architecture and contributor checks](Documentation~/architecture.md)
- [UI Toolkit tools](Documentation~/ui-toolkit.md)
- [Runtime inspection](Documentation~/runtime-inspection.md)
- [2.0 migration and rollback](Documentation~/migration-2.0.md)
- [Changelog](CHANGELOG.md)
- [MIT license](LICENSE.md)

The bundled IBM Plex Sans Thai Looped font is licensed separately under the SIL Open Font License 1.1
in `Editor/Fonts/OFL.txt`.
