# UI Toolkit tools

The four UI Toolkit tools provide a bounded inspect, validate, update, and Play Mode evidence loop.

| Tool | Purpose | Write gate |
|---|---|---|
| `unity_uitk_inspect` | Read UXML, USS, or a live `UIDocument` tree | No |
| `unity_uitk_validate` | Validate source structure and supported references | No |
| `unity_uitk_apply` | Preview or commit up to eight source changes | Commit only |
| `unity_uitk_playtest` | Snapshot/status or perform a semantic control action | Actions only |

## Safe source updates

Call `unity_uitk_apply` in `plan` mode with expected per-file hashes. Review the resulting plan and
`planHash`, then send the identical changes in `commit` mode with that hash. Commit refuses changed
inputs, paths outside the project UI sources, oversized payloads, and reparse-point traversal. It
uses adjacent temporary and backup files, verifies writes, and attempts full rollback on failure; it
does not claim filesystem-wide atomicity.

## Semantic Play Mode checks

Snapshot and status are read-only. Click, text, toggle, and focus actions require Play Mode and the
write gate. Actions resolve a live `UIDocument` element and invoke the supported semantic behavior;
they do not synthesize operating-system mouse, touch, keyboard, or controller input.

Use the tool evidence together with Unity Test Runner coverage and manual checks at target
resolutions, aspect ratios, safe areas, input methods, localization lengths, fallback glyphs,
contrast settings, and reduced-motion preferences.
