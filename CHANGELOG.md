# Changelog

Notable changes are documented here using [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and [Semantic Versioning](https://semver.org/).

## [2.1.0] - 2026-09-05

### Added

- Published all 73 shared tools, including UI Toolkit, through an optional Native Unity MCP adapter with matching input schemas and the existing write gate.
- Added a Connections window for Native status, tool counts, client setup, and explicit read-only starts without an extra Node process.
- Native screenshots return verified local PNG paths; use Node/TCP for inline images because Unity's tested Native relay drops image blocks.

### Changed

- Activity now identifies Native, Node/TCP, Pipeline, and Editor requests; Pipeline discovery includes full tool schemas.
- Scoped write permission to each Editor session so connecting one project read-only cannot change another Editor's write state.
- Documented Native compatibility, separate permissions for Unity's built-in tools, and the Unity CLI migration path.

## [2.0.2] - 2026-09-05

### Fixed

- Retried transient Windows presence-file replacement failures and limited repeated warnings.
- Refreshed the inbound command view automatically and clarified that official Unity MCP traffic uses a separate transport.

## [2.0.1] - 2026-09-05

### Fixed

- Restored UI Toolkit playtest compilation on Unity 6000.5 and newer by using the supported object identity API.

## [2.0.0] - 2026-09-04

### Breaking

- Renamed the UPM identity from `com.mcpbridge` to `com.villadiego.ai-mcp-unity-server` and the C# namespace/assembly to `AIUnityMCPServer`. Follow the [2.0 migration guide](Documentation~/migration-2.0.md) and replace the old manifest dependency; do not install both identities together.

### Added

- Added four bounded UI Toolkit tools for inspection, validation, optimistic source updates, and semantic Play Mode checks.
- Added Unity Pipeline commands `ai_mcp_list_commands` and `ai_mcp_dispatch` so the official Unity CLI/MCP path can use the same dispatcher beside the Node transport.

### Changed

- Setup now creates and reuses a per-user, content-addressed Node runtime cache for immutable Git and registry installs, with multi-Editor discovery and a read-only Doctor.
- Refreshed package metadata, documentation, license, and release validation for versioned OpenUPM distribution.

### Fixed

- Hardened instance selection, stale-presence handling, write-gate classification, UI Toolkit path checks, and rollback reporting.

## [1.0.0] - 2026-08-07

- Initial Unity 6 Editor package with an in-Editor chat, Node MCP transport, scene and asset tools, runtime diagnostics, performance inspection, and opt-in write commands.
- The original package identity was `com.mcpbridge`; it is retained here only as release history for 2.0 migration.

[2.1.0]: https://github.com/villadiego2020/ai-mcp-unity-server/compare/v2.0.2...v2.1.0
[2.0.2]: https://github.com/villadiego2020/ai-mcp-unity-server/compare/v2.0.1...v2.0.2
[2.0.1]: https://github.com/villadiego2020/ai-mcp-unity-server/compare/v2.0.0...v2.0.1
[2.0.0]: https://github.com/villadiego2020/ai-mcp-unity-server/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/villadiego2020/ai-mcp-unity-server/releases/tag/v1.0.0
