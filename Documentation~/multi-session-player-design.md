# Multi-session connections and managed game Players

Status: managed Players v1 is implemented and qualified on Windows Editor Unity 6000.0.75f1. Main integration adds immutable conflict previews, explicit journaled publication, import/compilation tracking, trusted local review and pristine-source workspace refresh. Qualification of the complete publication/compile/reload and pinned-Player flow requires the dedicated integration evidence, not static compilation alone.

See [managed sessions](managed-sessions.md) for the implemented tool flow and exact limits. The sections below retain the original design goals and historical observations; they do not imply that every proposed extension is delivered. Acceptance check 6 requires independent actual Main integration and subsequent refresh/rebuild verification.

## Confirmed outcome

- Keep one Unity Editor open.
- Allow multiple AI sessions to connect concurrently.
- Give each testing session a separate windowed standalone Player process, with independent gameplay and restart.
- Show each Player's owner, elapsed time, state, build revision and close control in the Editor.
- Allow the owning session to close its Player and report user-initiated closure back to that session.
- Background Player processes are acceptable. Additional interactive Editors are unnecessary.
- Background compilation may use headless Unity Editor workers; only the main interactive Editor needs a visible UI.

## Observed baseline before the queue reliability patch

- `Server~/index.js` sends health probes through `/ping` during explicit connection/status calls. It has no periodic ping scheduler.
- The supplied Activity screenshot shows successful Node/TCP ping traffic. The inspected persisted Activity snapshot also contained only successful pings; it does not establish a disconnect cause or caller identity.
- `Editor/MCPHandlers.cs` records transport source, but no caller session identity. Native also passes only source, command path and body.
- `Editor/MCPServer.cs` accepts concurrent TCP requests, but commands share Editor state, write permission and a global 25-command/second limit.
- Ordinary Node requests time out after 8 seconds and may retry up to eight attempts. Main-thread dispatch waits up to 20 seconds, and a timed-out queued action is not removed. Some writes explicitly disable retries, but this is not universal.
- Presence heartbeat depends on `EditorApplication.update`; discovery treats modern presence older than 15 seconds as stale. A busy Editor can therefore appear unavailable. This is a code-supported failure mechanism, not a confirmed explanation of the reported incident.
- Native and Pipeline use the shared dispatcher. Unity built-in tools retain their own permissions and logging. Registration compatibility is not a global ordering or session-isolation guarantee.
- Existing UI Toolkit playtests target the live Editor runtime. Separate run IDs do not isolate gameplay.
- The package currently contains an Editor-only assembly. Existing build and screenshot tools are not a standalone Player control channel.

## Ownership and flow

`AI session -> transport adapter -> Editor session registry -> Player supervisor -> standalone Player companion`

- The Editor owns the authoritative session/Player registry and dashboard. Transport adapters attach an authenticated connection identity; a caller-supplied display name is not ownership proof.
- Each connection receives an opaque session handle. Reconnection uses a bounded resume lease. Node process ID alone cannot identify a chat when a host shares a bridge across chats.
- Native/CLI adapters must establish whether caller identity and notifications are available. Otherwise they explicitly require an issued session capability; they must not guess identity from tool parameters or source labels.
- The Player supervisor owns only processes it launched and records process identity, start time, build ID and run ID. It never closes an unrelated game process by executable name.
- A small optional runtime companion is included only in managed test builds. It reports readiness and supplies bounded screenshots, input and shutdown over a loopback control channel with a per-run capability.
- The supervisor and companion use versioned envelopes, request IDs, deadlines, message-size limits and bounded queues. Capability values stay out of Activity and command-line logging.

## Connection reliability first

- Distinguish transport liveness, Editor readiness and job progress. A successful background ping cannot prove the Unity main thread is ready.
- Record source, issued session ID, request ID, queue duration, execution duration and structured failure reason.
- Summarize successful health traffic in Activity, with an opt-in expanded view; retain failures and actual tool operations.
- Use bounded async jobs for operations that can outlive a transport call. Expired queued mutations must not begin later.
- Retry reads when safe. Mutations require request deduplication with retained results or an explicit unknown-outcome response; never blindly replay a timed-out write.
- Apply session fairness and global backpressure. Serialize global Editor operations such as compilation, scene changes, builds and Play Mode transitions.
- Test long imports and domain reload separately from an actual dead process. Do not use a stale presence file as the sole proof that an Editor is dead.

## Player lifecycle and API

- Proposed operations: register/resume session, list sessions, request build, open/list/status/capture/input/close Player, and read session events.
- Build and launch are separate async jobs. Build once and reuse an immutable artifact for compatible sessions. Show the artifact revision; running Players do not automatically execute later Editor code changes.
- Start with Windows standalone windowed Players. Mobile emulation, OS window embedding and other platforms are separate extensions.
- Suggested initial limits: two Players, 640x360 resolution and a configurable frame cap. Hardware evidence must determine higher limits.
- States: queued, building, starting, running, closing, closed or failed. Track started time and last contact separately.
- User close, owner close, process crash, lease expiry and Editor shutdown produce distinct terminal reasons. A close is idempotent and has one terminal event.
- The dashboard shows active count, owner label/ID, run ID, PID, elapsed time, resolution, build revision, health and a close button. Each standalone window visibly identifies its session/run.
- Closing a window or Player B must not stop Player A or change the main Editor's Play Mode. Observe process exit independently of companion shutdown.
- Prefer graceful exit, with a bounded force-stop fallback restricted to a verified owned process. Define an orphan watchdog and reconnect policy across domain reload and Editor crash.
- Use TTL/lease cleanup with a documented grace period so a transient MCP reconnect does not immediately destroy a test run.

## Input, storage and notification boundaries

- Route input to the target Player companion. Parallel tests must not compete for the desktop's single keyboard/mouse focus. Declare supported input backends; return unsupported capability errors instead of silently injecting global OS input.
- An initial generic adapter can support declared Input System configurations; legacy/custom game input requires an explicit adapter. UI Toolkit semantic actions do not prove real player input behavior.
- Capture frames in the target Player and return run ID and frame metadata. Never substitute the main Editor Game view for a requested Player frame.
- Separate processes isolate in-memory game state, but do not automatically isolate PlayerPrefs, save files, accounts, ports or backend services. Expose per-run storage/network configuration through a game adapter and disclose the actual isolation capability.
- Store terminal events in a bounded sequenced per-session event stream with cursor/ack support. Use MCP notifications when supported and event reads as the fallback.
- A notification reaching the client does not guarantee that its AI starts another turn. Client wake-up support is a separate capability, and the UI must not promise it unconditionally.
- Launch/build/input/reset require explicit write authorization. Owner cleanup remains available for an existing managed run when the write switch is later disabled. Other sessions cannot close or control that run; the local user can.
- A session handle is not a sandbox against arbitrary Editor code execution or built-in Unity tools. Those broader capabilities retain their own authority boundaries.

## Per-session compilation and collision prevention

- A standalone Player executes a completed build; it is not a Unity compiler or asset importer. Unity-driven builds use a separate headless Editor worker with `-batchmode`, an explicit project path/build target and a dedicated log.
- A build worker cannot open the same project directory already open in the main Editor. Use a separate project snapshot/worktree for each active worker, with private `Library`, `Temp`, build output and mutable caches. Do not bypass Unity's project lock.
- One session may own multiple sequential runs, but does not require an always-running compiler. A bounded worker pool serves a central build queue; begin with one worker and allow concurrent Player execution. Increase build concurrency only after measuring machine and installed-toolchain capacity.
- A build job pins source content, package lock, Unity version, target, build options and companion version into its artifact key. Capture supported dirty source explicitly; a Git commit hash alone does not identify uncommitted game changes. Exclude transient and secret files from snapshots.
- Apply an exclusive lease per worker workspace and output location, plus host resource limits. Builds for the same immutable key can share the completed artifact; cancelling one waiter must not cancel a build still needed by other sessions.
- The main Editor's compile/import/build/Play Mode transitions share one ordered coordinator. Worker import/build operations stay in their private workspaces. Never let a worker write into the main project's source or `Library`.
- Session source edits remain in their own workspace. Publishing them into main is an explicit integration operation with conflict checks; closing a Player never automatically merges source or overwrites the user's unsaved Editor changes.
- Session code and asset edits must target that private workspace from the start. Process separation alone cannot isolate sessions that edit the same source files. Do not share writable `Assets`, `Packages`, project settings or `Library` through links between session projects.
- Private edits follow `session workspace -> that session's worker import/compile/build -> replacement Player`. File edits are immediate; a generic running Player needs a rebuild/restart to see new code/assets. Hot reload or live asset streaming requires a separately supported game capability.
- Main publication follows `explicit source integration -> conflict/validation checks -> main import/compile`. Other sessions retain pinned source/artifact revisions until an explicit sync and rebuild; publishing to main never silently replaces their running games.
- Provide close policies: close only (default), rebuild and reopen, or opt-in automatic rebuild after accepted source changes. Coalesce bursts by artifact key, cap retries, and report queued/building/failed/restarting states.
- A successful rebuild starts a new process with a new run ID and a reference to the prior run. A failed rebuild leaves other sessions running and retains the last successful artifact. Old processes retain their original artifact until explicitly replaced.
- If the user requests a main-project recompile, enqueue a single refresh/compile job after any authorized source integration. Distinguish this from producing a new standalone Player build.
- Persist job/run identity and event cursors across main Editor domain reload. On recovery, verify owned process start time and workspace lease before adopting or stopping anything; do not infer ownership from PID alone.
- Bound queued jobs, disk usage, worker lifetimes, logs and retained artifacts. Never delete a workspace or artifact with an active build/run lease. Unity/toolchain availability may limit worker concurrency; report startup failures without changing licenses or installing components automatically.

## Delivery and acceptance checks

1. Diagnose and stabilize connections: correlate callers, reproduce Editor-busy timeout behavior, verify no late/duplicate writes, verify fair concurrent reads and reload recovery. Preserve existing Native/Node/CLI schema and write-gate behavior.
2. Deliver managed Players: two sessions launch distinct processes from one artifact. Show both owners and elapsed times. Closing by user, owner or OS window affects only the selected run and retains a retrievable closure event. The main Editor remains usable.
3. Deliver actual per-Player tests: drive different inputs in A/B and verify different frames/state. Restart A while B continues. Verify supported games use separate save locations and test accounts; report unsupported isolation explicitly.
4. Exercise failures: build failure, stale artifact, rejected input, absent runtime companion, Player crash, request timeout, reconnect, domain reload and Editor exit. Prove bounded cleanup and no unowned process termination.
5. Prove build coordination: simultaneous session requests queue without shared workspace/output writes; identical artifact requests deduplicate; a failed or cancelled build does not interrupt another session; main source remains unchanged until explicit integration; close-only and rebuild/reopen policies produce distinct observable results.
6. Prove source isolation: change code and an asset in A, rebuild A, and verify A's frame/behavior changes while main and B's files and running behavior remain unchanged. Integrate A into main with conflict checks; B remains pinned until explicitly synchronized and rebuilt.

Implementation needs the Unity lead and independent runtime verification. Existing compilation or registration tests cannot prove these user outcomes.

## Unity references

- [Standalone Player command-line arguments](https://docs.unity3d.com/6000.0/Documentation/Manual/PlayerCommandLineArguments.html)
- [Application.persistentDataPath](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Application-persistentDataPath.html)
- [PlayerPrefs](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/PlayerPrefs.html)
- [Multiplayer Play Mode overview](https://mp-docs.dl.it.unity3d.com/mppm/current/about/)
- [Build a Player from the command line](https://docs.unity3d.com/6000.0/Documentation/Manual/build-command-line.html)
- [Unity Editor command-line arguments and project locking](https://docs.unity3d.com/2022.3/Documentation/Manual/EditorCommandLineArguments.html)
