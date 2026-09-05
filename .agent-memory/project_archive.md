# BUGS

- [CMM-009 Harden presence heartbeat and clarify MCP activity logging](archive/CMM-009.md) — Transient Windows presence writes now retry with bounded delay, repeated warnings are throttled, and the inbound view clearly separates custom TCP from official Named Pipe traffic; 26 Node tests and Unity 6 static compilation passed. Watch: live antivirus-lock timing and visual repaint remain environment-dependent.

# IMPROVE / OPTIMIZE

- [CMM-001 AI Unity MCP Server self-healing setup](archive/CMM-001.md) — Worker registry pollution, stale port/config state, and ambiguous targets fixed with identity-based reconnect + Codex Setup/Doctor; canonical English-only branding guarded by 13 Node tests; commit `f5b42fc`. Watch: Unity EditMode execution still depends on a healthy UPM/testables host.

# REFACTOR

- [CMM-005 Rename folder and project to ai-mcp-unity-server](archive/CMM-005.md) — Repository, Node package, consumer paths, Codex MCP registration, and tracker moved to the canonical slug while Unity compatibility IDs remain stable; 14 Node tests and static C# compilation passed. Watch: delete the empty old folder after this Codex task closes.

# FEATURE

- [CMM-010 Native Unity MCP integration](archive/CMM-010.md) — Version 2.1.0 exposes all 73 tools via optional Native registration, adds Connections and shared Activity, and isolates write permission per Editor; 81 Unity tests, 26 Node tests and 7 relay checks passed. Watch: Native screenshots use verified local files; Unity deprecates this Native API in favor of CLI.

- [CMM-002 Unity CLI and pipeline integration](archive/CMM-002.md) — Official Pipeline 0.6.0-exp.1 now exposes two native commands through the existing dispatcher without replacing Node/TCP; static Unity 6 compilation and 17 Node tests passed. Watch: live discovery remains blocked by the local UPM host error.
- [CMM-004 UI Toolkit capability pack](archive/CMM-004.md) — Four bounded inspect/validate/apply/playtest tools added with fail-closed write gating, deterministic plan hashes, rollback, and screenshot evidence; 17 Node/MCP tests and Unity 6 static compilation passed. Watch: Unity Test Runner runtime is blocked by the local UPM host error.

# ANALYSIS

(None)
