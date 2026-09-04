# AI Unity MCP Server

![Unity 6000.0+](https://img.shields.io/badge/Unity-6000.0%2B-222222)
![Editor only](https://img.shields.io/badge/scope-Editor--only-2d6cdf)
![69 Editor commands](https://img.shields.io/badge/commands-69-2d6cdf)

**Let an AI assistant look at your Unity Editor — and, when you allow it, click the buttons for you.**

AI Unity MCP Server is an Editor-only package for **Unity 6** with two ways in:

- **A chat window inside Unity** (`AI Unity MCP Server → Chat`, or press `F12`). You ask a question; the window
  collects the real Editor data first and answers from that, not from guesswork.
- **An MCP server** (a small Node program in `Server~/`). It lets an AI agent running *outside* Unity —
  Claude Code in your terminal, or any other MCP client — inspect the same Editor and run the same
  commands.

Both go through the same C# dispatcher: one command list, read-only by default, one write gate.
Nothing from this package ships in your game build — all of it lives in an Editor-only assembly.

<details>
<summary><b>New to MCP?</b></summary>

The Model Context Protocol is a standard way for an AI assistant to use tools on your computer. A
*server* publishes a list of actions it can perform; a *client* (the AI) reads that list and calls the
ones it needs. AI Unity MCP Server exposes Unity Editor operations — read the Console,
open a scene, run a performance audit, take a screenshot, and so on.

</details>

---

## Contents

- [Why it exists](#why-it-exists)
- [Requirements & install](#requirements--install)
- [First run](#first-run)
- [Everyday use](#everyday-use)
- [External MCP clients](#external-mcp-clients)
- [Command catalogue](#command-catalogue)
- [Safety](#safety)
- [Known rough edges](#known-rough-edges)
- [More documentation](#more-documentation)
- [Credits & license](#credits--license)

---

## Why it exists

When you ask an AI for help with a Unity problem today, **you** are the one carrying the data back and
forth:

| Without AI Unity MCP Server | With AI Unity MCP Server |
|---|---|
| You copy Console errors and paste them into a chat | It reads the Console itself |
| You describe the scene, or screenshot the Hierarchy | It reads the hierarchy itself |
| You read the Profiler and summarise it in your own words | It runs an audit and reads the numbers |
| You take the answer back and click through the Inspector | It can make the change — if you allow writes |

A few things that means in practice:

- Type `fps why does this scene stutter?` and a real performance audit runs *before* the model answers.
- Ask an external agent to fix a bug without describing your scene first — it reads the live Editor.
- Select an object, type `watch health`, press Play, and see the value trend live while the game runs.

This package was pulled out of a production Unity project and made standalone, so it is opinionated in
places. Skim [Known rough edges](#known-rough-edges) before judging it as a general-purpose tool.

---

## Requirements & install

| What | Needed for | Notes |
|---|---|---|
| **Unity 6** | everything | `package.json` declares `6000.0`; developed and tested on `6000.0.75f1` |
| **An Anthropic API key** *or* **the Claude Code CLI** | the in-editor chat | CLI route: `npm i -g @anthropic-ai/claude-code`, then `claude login` |
| **Codex CLI** | external Unity tools in Codex | install and sign in once; Unity can register the bridge through `codex mcp` |
| **Node.js 18+** | the external AI Unity MCP Server bridge only | the bridge uses ESM and global `fetch` |
| `com.unity.test-framework` *(optional)* | `run_tests` | without it that one assembly is skipped and the two test commands return a clear error; nothing else changes |
| Photon Fusion 2 *(optional)* | `fusion_stats` | network readers are reflection-based and report zero when Fusion is absent, so single-player projects are unaffected |

Pick an install option by what you plan to do with the package:

| Option | Installs from | The package is | Pick it when |
|---|---|---|---|
| **A** | Git URL | read-only, in `Library/PackageCache` | you just want to use AI Unity MCP Server — simplest path |
| **B** | Local `file:` reference | editable, stays in the folder you cloned | you edit or contribute to the package source |
| **C** | A copy inside `Packages/` | editable, versioned with that project | you want the package to travel with one project |

**Option A — Git URL.** **Window → Package Manager → `+` → Install package from git URL…** (called
*Add package from git URL…* before Unity 6), then paste:

```
https://github.com/Smile-Codes/mcpbridge.git
```

`package.json` sits at the repository root, so no `?path=` suffix is needed. Package Manager shells out
to your Git client, so Git 2.14+ must be installed and on `PATH` — otherwise the install fails with a
*"no 'git' executable was found"* error rather than anything about this package. Append `#<revision>`
to pin a tag, a branch or a full 40-character commit SHA (`…mcpbridge.git#v1.0.0`,
`…mcpbridge.git#main`). No release tags are published yet, so a bare URL resolves the default branch
once, writes that commit into `Packages/packages-lock.json`, and keeps the whole team there until
someone presses **Update**.

The external AI Unity MCP Server bridge works with this read-only install too. **Configure Codex** copies only the
small bridge application into a versioned per-user cache and installs dependencies there; it never
writes `node_modules` into `Library/PackageCache`.

<details>
<summary><b>Options B and C — editable installs</b></summary>

**Option B — local `file:` reference.** Add this to the consuming project's `Packages/manifest.json`,
adjusting the relative path to wherever this folder lives (an absolute path such as
`"file:C:/Work/git/com.mcpbridge"` works too):

```json
{
  "dependencies": {
    "com.mcpbridge": "file:../../com.mcpbridge"
  }
}
```

The package stays editable at its source folder and several projects can share one clone, so this is
the setup for contributing.

**Option C — embedded package.** Copy this whole folder into the project's `Packages/` directory. Also
editable, but the copy belongs to that one project.

</details>

---

## First run

**1. Open the chat window.** Open Unity, wait for the compile to finish, then open
**AI Unity MCP Server → Chat** (or press `F12`). The window opening at all means the package installed
correctly.

**2. Start the server.** In the chat window, go to the **Claude In** tab and press **▶ Start** (the
menu item **AI Unity MCP Server → Server → Start** does the same thing). The status pill in the header turns
green / *online*. To start it automatically on future Editor launches, enable **AI Unity MCP Server → Server →
Auto Start Read-Only**. Automatic and remote starts always reset **Allow Write Commands** to off.

> [!IMPORTANT]
> The server is required **even for the in-editor chat** — the chat refuses to send while it is off.
> It restarts itself after a script recompile. A Codex session can also start it with `unity_connect`,
> or you can opt in to Auto Start Read-Only. It listens on `127.0.0.1:23457`.

**3. Pick a backend.** The gear icon opens Settings:

| Backend | What it uses | Trade-off |
|---|---|---|
| **API Chat** | an Anthropic API key, stored in `EditorPrefs` — never in the project, so it cannot end up in git | pay per token; model is selectable (Sonnet by default) |
| **Subscription** | the Claude Code CLI in print mode, so it runs on your Claude subscription | model and effort are selectable; slower per message, because every message pays the CLI cold start |

**4. Sanity check.** Type `test` in the chat. It answers locally, with no model call, listing the
server status, the write mode, and every available command.

**5. Turn on writes when you need them.** **AI Unity MCP Server → Allow Write Commands** is **off by default**.
Until you switch it on, every command that touches the scene, assets, Play Mode or the build is
blocked. Read-only commands work either way.

---

## Everyday use

**Chasing down a stutter**

1. Press `F12`, check the header says *online*, and type `fps why does this scene stutter?`
2. The window sees `fps`, runs `perf_audit` on your live Editor, and sends those numbers along with
   your question.
3. The answer arrives in two versions — a **Dev** section and an **Art** section, so the same finding
   reads as "fix this in C#" or "fix this in the asset". The role chip in the header switches between
   them.
4. If the model needs more data it replies with a command block. The window runs it through the same
   dispatcher and the same write gate, then summarises the result in plain language. Screenshots come
   back as images the model can actually look at.

**Watching a value during Play Mode**

1. Select the object and type `watch health` — only the field name is required; the component is
   auto-detected and the object defaults to your selection.
2. Enter Play Mode and open the **👁 Watch** panel: current value, trend arrow, sparkline of recent
   history, alert badge, quick-add for the selected object, per-item delete.
3. `wv` prints current values into the chat; `watchclear` removes all watches.

Watches and the event probe only sample values, so they work with writes off. Entering Play Mode from
chat (`play_control`) needs writes on.

**Also in the window**

- `@` autocompletes project scripts, `#` prefabs, `/` locally installed Claude skills and slash
  commands (Subscription mode). `Ctrl+V` pastes an image from the clipboard, **+ Image** opens a file
  picker.
- **Monitor** panel — a background health watcher that logs memory spikes and Editor stalls to
  `Library/AIUnityMCPServer/monitor.log`.
- **Claude In** tab — every MCP command that hit this Editor (path, body, response, duration, error
  flag), persisted to `Library/AIUnityMCPServer/mcp_log.json`. This is where you look when an external agent is
  driving and you want to see what it did.

<details>
<summary><b>Keyword shortcuts — words that fetch real data before the model answers</b></summary>

Type them anywhere in your sentence. Repeating keywords that map to the same command collapses into a
single call. The **🔑 Keys** button in the toolbar lists the main ones inside Unity.

| Type any of these | It runs first | Good for |
|---|---|---|
| `fps` `perf` `audit` `spike` `stutter` `frame drop` | `perf_audit` | frame stats, heavy objects, batching |
| `gc` | `perf_audit` | allocation pressure |
| `mem` `memory` `ram` | `memory_snapshot` | Mono heap, native, graphics memory |
| `console` `errors` `err` | `read_console` | recent errors and warnings |
| `exceptions` `exc` | `get_exceptions` | runtime exceptions with stack traces |
| `log` | `read_logfile` | full stack traces from `Editor.log` |
| `hier` `hierarchy` | `scene_hierarchy` | the object tree of the open scene |
| `sel` `selection` | `get_selection` | what is selected right now |
| `state` | `capture_state` | isPlaying / timeScale / frameCount |
| `wv` `watches` | `watch_get` | current values of everything you watch |
| `draw` `batches` `setpass` `overdraw` `lod` `shadow` `light` | `perf_audit` | rendering cost (art side) |
| `fusion` | `fusion_stats` | Photon Fusion 2 stats (Play Mode only) |

A few heavy scans — `refactor`, `tex`, `unused`, `deep` — deliberately do **not** auto-run. The model
calls them on purpose when they are warranted, so a stray word cannot freeze your Editor.

</details>

---

## External MCP clients

The recommended path is Codex-first and only needs to be configured once per machine.

**1. Install the package by Git URL, registry, local reference or as an embedded package.** All four
locations support the external bridge. Git and registry packages remain read-only in
`Library/PackageCache`; Setup does not modify them.

**2. In Unity, run `AI Unity MCP Server → Setup → Configure Codex`.** Setup validates Node and Codex, fingerprints
the bridge source, then atomically bootstraps it under
`<LocalApplicationData>/AIUnityMCPServer/runtime/<package-version>-<content-hash>`. It runs `npm ci` from
the cached lockfile, never from the package, and registers the stable cached `index.js`
through the official `codex mcp` CLI. The cache contains the bridge application and npm dependencies,
not a bundled Node runtime; Node 18+ still needs to be installed on the machine.

Running Setup again validates the existing cache and does not recopy files, reinstall dependencies or
rewrite a healthy registration. If a different server already owns the `AIUnityMCPServer` key, Setup leaves it
untouched and tells you to use **Repair Codex Registration** only when replacement is intentional.
Repair asks before replacing that key. A package update creates a coexisting cache directory and
Configure automatically switches a registration that is provably one of this package's older managed
caches. Old content-hash directories remain available so already-running sessions can finish.

If dependency installation fails offline, reconnect and run Configure again. The completed bootstrap
is reused, while the missing dependency step alone is retried. **Doctor** reports package-source,
runtime-bootstrap and runtime-dependency failures separately and prints the exact cache path.

Restart Codex once after first registration so it refreshes its tool list. This global registration is
available to new projects and new sessions; it does not need to be generated again per project.

**3. Connect from the agent.** Call `unity_connection_status`, then `unity_connect`. The latter finds
the intended Editor, starts its server read-only when needed, follows port changes after a domain
reload, and verifies `ping` in one call. When several projects match, it returns `AMBIGUOUS` with exact
`instanceId` values instead of silently choosing the wrong Editor. Pass one of those IDs back to
`unity_connect`.

`unity_list_instances`, `unity_select_instance`, and `unity_start_instance` remain available for
existing workflows. All manifest commands retain their names (`read_console` becomes
`unity_read_console`). `UNITY_PROJECT_PATH` can scope automatic discovery to one project;
`UNITY_MCP_PORT` remains a fixed-port escape hatch.

For compatibility with MCP clients that read project files, Setup also maintains a minimal
`<UnityProject>/.mcp.json` pointing at the same stable cache (editable installs can create it directly
on load). Codex does not use that file; its config is managed only through `codex mcp`. Custom
`.mcp.json` files are never overwritten. Run **AI Unity MCP Server → Setup → Doctor** for a read-only report
with actionable fixes.

---

## Command catalogue

**69 Editor commands**, defined in `Server~/commands.json` — the single source shared by the Node
bridge and the C# dispatcher — plus **5 connection-management tools** that live in the bridge itself.

| Group | Commands | What that group is for |
|---|---|---|
| Scene & objects | 14 | list/open/save scenes, read the hierarchy, create and edit objects |
| Assets | 12 | find assets; create prefabs, materials, UI, terrain; tune ScriptableObjects |
| Diagnostics | 8 | Console, `Editor.log`, exceptions, log alerts, Play state |
| Performance | 8 | scene audit, frame spikes, GC/CPU hot spots, memory, textures, UI |
| Play Mode inspection | 12 | Play control, value watches, event probe, raycast, navmesh path |
| Code & project | 10 | read/edit scripts, run C#, refactor audit, compile, tests, build |
| Other | 5 | screenshots, batching, ping/stop, Fusion stats |
| Connection *(bridge-side)* | 5 | status/connect plus backward-compatible list, select and start tools |

Commands that *change* something (create, delete, set, edit, Play Mode control, batch, build, tests)
are blocked until **Allow Write Commands** is on — see [Safety](#safety). Everything else reads.

<details>
<summary><b>Show every command</b></summary>

### Scene and objects

| Commands | What they do |
|---|---|
| `scene_list` `open_scene` `save_scene` | List the project's scenes, open one, save the open one |
| `scene_hierarchy` | The object tree of the open scene |
| `count_components` | Component census, split into active vs. pooled/inactive |
| `inspect_object` | Serialized values of an object — or everything, via reflection, with `deep=true` |
| `create_gameobject` `delete_gameobject` `set_transform` | Create, delete and move objects |
| `add_component` `set_property` | Add a component; set a primitive value on one |
| `assign_reference` | Assign an object or asset reference (`set_property` cannot do this) |
| `get_selection` `set_selection` | Read or change the Hierarchy selection |

### Assets

| Commands | What they do |
|---|---|
| `find_asset` | Locate assets in the project |
| `create_prefab` `place_prefab` | Turn an object into a prefab; drop a prefab into the scene |
| `create_material` `create_sprite_atlas` | Create a material; create a sprite atlas |
| `create_ui` | Create UI objects |
| `create_terrain` `set_terrain_heights` | Create terrain; set heights from Perlin noise or a raw heightmap |
| `read_scriptableobject` `edit_scriptableobject` | Read and tune config / balance data on a ScriptableObject asset |
| `set_import_settings` | Texture importer settings |
| `delete_asset` | Move an asset to the OS trash — refuses folders and anything outside `Assets/` |

### Diagnostics

| Commands | What they do |
|---|---|
| `read_console` `clear_console` | Read the Console; clear it |
| `read_logfile` | Tail of `Editor.log`, with full stack traces |
| `get_exceptions` | Deduplicated rolling buffer of runtime exceptions |
| `console_alert` `console_alert_get` `console_alert_clear` | Count log messages matching a pattern, so errors that scroll away during Play are still caught |
| `capture_state` | `isPlaying`, `timeScale`, `frameCount` — call it twice to see whether frames are advancing |

### Performance

| Commands | What they do |
|---|---|
| `perf_audit` | Scene census: renderers, skinned meshes, particles, realtime lights, animators, mesh colliders, heavy object groups, heuristic warnings, captured frame spikes |
| `perf_worst` | The worst captured spike on its own |
| `diagnose_deep` *(`unity_deep_analysis`)* | Top GC allocators and CPU self-time, with the offending source lines |
| `memory_snapshot` | Mono heap, Unity native, graphics driver memory, GC collection counts |
| `audit_textures` | Textures worth optimising: oversized, uncompressed, read/write enabled |
| `audit_unused` `audit_empty_folders` | Assets that *may* be unused (report only — Addressables/Resources are not detected) and empty folders under `Assets/` |
| `optimize_ui` | Turn off `raycastTarget` on non-interactive Image/Text, turn off `pixelPerfect` on Canvas, warn about heavy LayoutGroups |

### Play Mode inspection

No changes to your game code required. Everything here only reads live state — except `play_control`,
which drives Play Mode itself and therefore needs writes on.

| Commands | What they do |
|---|---|
| `play_control` | Enter / exit / pause / resume / step, plus `timescale` for slow motion |
| `watch_add` `watch_get` `watch_clear` | Sample a field every 0.5 s and track its trend; only the field name is required |
| `watch_alert` | Rising-edge condition (`lt` / `gt` / `eq` / `changed`) — logs a warning and counts hits |
| `watch_animator` | Current animator state, or one parameter |
| `event_log` `event_log_get` `event_log_clear` | Temporary probe that records OnCollision / OnTrigger events; detaches on Stop |
| `raycast` `overlap` | Physics ray; colliders inside a sphere |
| `navmesh_path` | Path status, corners and distance — for AI that cannot reach its target |

### Code and project

| Commands | What they do |
|---|---|
| `read_script` | Source with line numbers, optionally just one method |
| `edit_script` | Targeted find/replace, not a whole-file rewrite |
| `run_csharp` | Escape hatch: compile and run C# against the live Editor via Roslyn |
| `refactor_audit` | Large classes, long/complex methods, fan-in/fan-out coupling, deep inheritance, public fields, magic numbers, TODO debt |
| `compile` `compile_status` | Trigger a compile; poll its result |
| `run_tests` `get_test_results` | Unity Test Runner (EditMode or PlayMode) with async polling |
| `build_player` `git_status` | Build the player; read git status |

### Other

| Commands | What they do |
|---|---|
| `capture_screenshot` | Game or Scene view. The bridge reads the PNG off disk and returns it as a real image block, so the model *sees* the result instead of a file path |
| `run_batch` | Up to 50 commands in one round trip |
| `ping` `server_stop` | Liveness check; stop the server |
| `fusion_stats` | Photon Fusion 2: tick, RTT, bandwidth, packet loss, resimulation count |

### Multi-Editor (bridge-side)

`unity_connection_status` · `unity_connect` · `unity_list_instances` · `unity_select_instance` · `unity_start_instance`

Every open Editor registers itself, so an agent can list them (ParrelSync Main/Clone setups included),
pick one, and even switch a stopped one on. `unity_connect` is the normal entry point; the other three
management tools remain backward compatible.

</details>

---

## Safety

- **Read-only by default.** Mutating routes are refused with an explanatory error until **Allow Write
  Commands** is on. The gate applies to the in-editor chat and to external MCP clients alike —
  including every sub-command of `run_batch`.
- **Rate limited** to 25 commands per second, so a runaway agent loop cannot hammer the Editor.
- **Deletes are recoverable.** `delete_asset` moves files to the OS trash and refuses folders,
  third-party paths and anything outside `Assets/`.
- **Non-idempotent commands are never retried.** Script edits, `run_csharp`, batches, deletes, import
  settings, builds and test runs are flagged `noRetry`, so a timeout cannot silently re-run side
  effects.

> [!WARNING]
> **Local, but not authenticated.** The listener binds `IPAddress.Loopback`, so it is unreachable from
> the network — but there is no authentication. Any process on your machine that can reach the port can
> drive the Editor while the server is on. Turn it off when you are not using it.

---

## Known rough edges

Stated up front so nothing surprises you after install.

- **The package is English-only.** Editor labels, tooltips, diagnostics, documentation and built-in
  analysis prompts use English consistently.
- **Live profiler recorders are switched off** by a constant (`ProfilerReader.ENABLED = false`), so
  Play Mode carries zero profiling overhead. The cost: the GC / Deep / Live buttons are hidden and live
  FPS and draw-call numbers are unavailable. Scene census, memory snapshots and frame-spike capture
  still work; naming the exact method behind a spike needs Unity's Profiler window to be recording.
  Flip the constant to get the full set back.
- **Unity maintains `<project>/.mcp.json` for legacy project-scoped MCP clients** — Codex registration
  is global and managed through `codex mcp`. The legacy file backs off from anything you customised
  and points Git/registry installs at the per-user cache only after Setup has bootstrapped it.
- **The Node bridge ships without its dependencies or a Node runtime.** Configure Codex installs npm
  dependencies into its per-user cache. Node 18+ remains an external prerequisite; bundling it is a
  separate distribution concern. The in-editor chat needs no Node at all.
- **Skills are standalone.** The picker scans only the current project and the user's skill directory;
  the analysis playbook can come from the current project and otherwise uses the embedded default.
- **Version 1.0.0, single-developer project.** No CI, no registry publication, no tagged release yet.

---

## More documentation

| Document | What is in it |
|---|---|
| [`Documentation~/architecture.md`](Documentation~/architecture.md) | Request flow, components, Editor discovery, `.mcp.json` rules, write-gate internals, repo layout, running the tests |
| [`Documentation~/runtime-inspection.md`](Documentation~/runtime-inspection.md) | Guide to the Play Mode inspection tools |
| [`CHANGELOG.md`](CHANGELOG.md) | Release history |

---

## Credits & license

Built with **Claude Code** (Anthropic) used as an AI pair-programmer throughout: the package is
maintained by a single developer, with Claude models writing and reviewing large parts of the code
alongside them. It also targets Claude on both backends — the Anthropic API and the Claude Code CLI.
Implemented on top of the [Model Context Protocol](https://modelcontextprotocol.io) and the official
`@modelcontextprotocol/sdk`. Not affiliated with, sponsored by, or endorsed by Anthropic.

Fonts under `Editor/Fonts/` (IBM Plex Sans Thai Looped) are licensed under the SIL Open Font License —
see `OFL.txt`. No license file is published for the package source itself yet.
