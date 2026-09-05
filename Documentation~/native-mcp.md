# Native Unity MCP setup

AI Unity MCP Server 2.1.0 can publish its complete 73-tool manifest inside Unity AI Assistant's
Native MCP server. The four UI Toolkit tools, scene/asset tools, diagnostics and test commands use
the same implementations as Node/TCP and Unity Pipeline.

## Connect

1. Install this package and Unity AI Assistant **2.18.0-pre.2 through 2.18.x**. The optional assembly
   activates only for this supported API range. Other installations retain Node and Pipeline tools.
2. Open **AI Unity MCP Server → Connections**. Confirm the project path and Editor instance.
3. Press **Start Native with Our Writes OFF**. This explicitly enables Unity's Native server and
   resets only this Editor's AI Unity MCP Server write permission. Existing tool selections are kept.
4. Press **Native Settings / Configure Client** and use Unity's client configuration flow. Select
   this exact project in your client. The relay is owned by Unity; our adapter launches no Node process.
5. Check the **AI Unity MCP Server** group. Newly registered tools default to enabled, but any
   previous group/per-tool disable remains in effect. Connections shows registered and available
   enabled counts separately and reports name collisions.
6. Refresh your client's tools, then call `unity_ping` and `unity_scene_hierarchy`. Open **Chat →
   Activity** to see the **Native** source. Enable **Allow Write Commands** only when you intend
   to modify this Editor's scene or assets.

The 5 Node-only connection tools are unnecessary on Native: Unity's relay owns project targeting and
connection discovery. `unity_server_stop` stops our TCP listener only; use Native Settings to stop
Unity's Native server. Using both transports is supported, but avoid duplicate client registrations
for the same tools unless you intentionally need both paths.

## What is shared

- Each manifest tool has its own Native name, description, required fields, enum values, array types,
  bounds and defaults. Unknown input keys are stripped consistently with Node's Zod contract.
- Every Native invocation reaches the existing dispatcher once, including rate limiting and the
  write gate. Errors remain errors. Native adds no automatic retry for timed-out writes.
- Results are returned in `data` and Unity's `structuredContent`. Screenshots include a local PNG
  path and `imageDelivery` metadata with the verified PNG signature and file size. The tested Unity
  relay drops inline image blocks: a client must open the file with its local image viewer, or use
  the existing Node/TCP connection to receive inline MCP images. Remote clients need access to the
  Editor's filesystem to open the Native screenshot path.
- Native, Node/TCP, Pipeline and Editor requests appear in Activity. Unity's built-in tools use
  their own permissions and logging; our write gate does not restrict those tools.
- Write permission belongs to the current Editor session. Reloading scripts preserves it; a new
  Editor starts OFF. Old global permission values are not migrated to an enabled state.
- Registration restores tools after Unity refreshes its registry and cleans up before reload/quit.
  It never replaces another tool with the same name or silently re-enables a disabled tool.

## UI Toolkit workflow

Use `unity_uitk_inspect` to read UXML/USS, `unity_uitk_validate` to check it, then
`unity_uitk_apply` with `mode=plan` for a read-only preview. Enable writes and commit using the returned
hash only after reviewing the plan. Use `unity_uitk_playtest` for a snapshot or an explicit interaction,
then poll its run ID. See [UI Toolkit tools](ui-toolkit.md) for request examples and limitations.

## Troubleshooting and migration

- **Native unavailable:** confirm the supported Assistant version and resolve compilation errors.
  Our package does not install or upgrade Assistant automatically.
- **Registered but not enabled:** inspect Unity's group and individual tool switches. Programmatic
  availability filters also affect the enabled count.
- **No connected clients:** run Unity's Native client setup for the project shown in Connections,
  then restart or reconnect that client. Starting the server alone does not attach a client.
- **Activity empty:** call a tool from the AI Unity MCP Server group; built-in Unity tool traffic
  is not captured by our dispatcher log.
- **Timeout after a write:** inspect the actual scene/assets before retrying; cancellation and
  cross-transport deduplication are not provided.

Unity's Assistant documentation deprecates Native MCP in favor of Unity CLI. Native is a supported
compatibility option here, not the only integration. For Unity CLI, target this project and call
`ai_mcp_list_commands` to obtain all tool schemas, then `ai_mcp_dispatch` with the selected command
and JSON body. Node setup remains available through Connections. See [architecture](architecture.md)
for lifecycle and dependency boundaries.
