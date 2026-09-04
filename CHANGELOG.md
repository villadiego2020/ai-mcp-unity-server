# Changelog

Notable changes are documented here using [Keep a Changelog](https://keepachangelog.com/en/1.1.0/)
and [Semantic Versioning](https://semver.org/).

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

[2.0.0]: https://github.com/villadiego2020/ai-mcp-unity-server/compare/v1.0.0...v2.0.0
[1.0.0]: https://github.com/villadiego2020/ai-mcp-unity-server/releases/tag/v1.0.0
