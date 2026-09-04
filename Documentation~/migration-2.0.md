# Migrate from 1.0.0 to 2.0.0

Version 2.0 changes the package ID, assembly name, and C# namespace. Back up or commit the Unity
project before migrating.

## Upgrade

1. Close Unity.
2. In `Packages/manifest.json`, remove `com.mcpbridge` and add
   `"com.villadiego.ai-mcp-unity-server": "2.0.0"` under the OpenUPM scoped registry shown in the
   [README](../README.md#install-version-200).
3. Remove only the old package's entry from `Packages/packages-lock.json`, or let Package Manager
   regenerate the lock after the manifest edit. Do not install both package identities together.
4. Replace source imports from `MCPBridge` with `AIUnityMCPServer` if project-owned Editor code used
   package internals.
5. Reopen Unity, wait for package resolution, then run **AI Unity MCP Server → Setup → Configure Codex**
   and **Doctor**. Setup selects the new 2.0.0 runtime cache without deleting a cache used by an older
   client process.
6. Start a new MCP client session so it reloads the tool list.

The `.mcp.json` server key stays `AIUnityMCPServer`, and public `unity_*` tool names stay stable.

## Roll back

1. Close MCP clients and Unity.
2. Replace the 2.0 dependency with the previous `com.mcpbridge` 1.0.0 source and remove the 2.0 package
   entry. Do not keep both installed.
3. Reopen Unity and allow Package Manager to regenerate its lock.
4. Run the previous package's setup to point the MCP client back to its runtime.

Project assets changed through write commands are not package migration data. Restore those changes
from version control separately when needed.
