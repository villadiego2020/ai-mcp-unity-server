# Runtime inspection tools

AI Unity MCP Server can inspect a running Unity scene without modifying gameplay code. These tools are intended
for short, evidence-driven debugging sessions in Play Mode.

## Runtime watches

`unity_watch_add` samples a field or property every 0.5 seconds and keeps the ten most recent values. Specify a
GameObject, component and field path, or omit the component to let the server find a matching game script.
Nested paths such as `Damageable.Hp.Value` are supported.

```json
{"objectName":"Player","field":"currentHp"}
```

Use `unity_watch_get` to read current values and trends. Use `unity_watch_clear` when the investigation ends.
Runtime-spawned objects can be matched by exact name, case-insensitive name or a contained name such as a prefab
name without its `(Clone)` suffix.

`unity_watch_alert` adds an edge-triggered condition. Supported operators are `lt`, `lte`, `gt`, `gte`, `eq`,
`ne` and `changed`. An alert is counted only when its condition changes from false to true, so a persistent
condition does not flood the Console.

```json
{"objectName":"Player","field":"currentHp","op":"lt","value":"0"}
```

`unity_watch_animator` watches either the active Animator state or one parameter. Use `parameter` for a named
parameter; omit it to sample the current state and normalized time.

## Collision and trigger events

`unity_event_log` attaches a temporary probe to a GameObject and records collision and trigger enter/exit events.
If no object name is supplied, the current Hierarchy selection is used. Read captured events with
`unity_event_log_get` and detach all probes with `unity_event_log_clear`. Probes are removed automatically when
Play Mode ends.

The target needs the normal Unity physics setup: a Collider, a Rigidbody where required, and `isTrigger` for
trigger callbacks. An empty event log often indicates a layer-collision rule or physics-component issue rather
than a logging failure.

## Console alerts

`unity_console_alert` watches Console messages for a case-insensitive substring and records match counts plus the
latest messages. Use `unity_console_alert_get` to poll results and `unity_console_alert_clear` to remove all
patterns. This is useful for intermittent warnings or errors that quickly scroll out of view.

## Play Mode control

`unity_play_control` can enter, exit, pause, resume or step Play Mode. It can also set `Time.timeScale`; values
between `0.1` and `0.25` are useful when observing fast combat, spawn or animation events. Restore the scale to
`1` after the investigation. Exiting Play Mode also restores the normal scale.

## Performance evidence

`unity_capture_state` reports Play Mode, pause state, time scale, frame count, FPS, network information and recent
spikes. Capture twice to determine whether the frame count is advancing.

`unity_perf_audit` provides a wider performance snapshot, while `unity_perf_worst` reports the worst captured
frame spike and its likely CPU or allocation contributor. `unity_memory_snapshot` reports managed, native and
graphics memory. `unity_fusion_stats` adds Photon Fusion measurements when a `NetworkRunner` is available.

The automatic spike monitor records only lightweight timing and allocation data during normal sampling. Expensive
call-tree inspection happens on demand so the monitor does not create the performance issue it is measuring.

## Suggested workflow

1. Enter Play Mode and reproduce the issue.
2. Add a focused field, Animator, event or Console watch.
3. Perform the action that triggers the problem.
4. Read the watch together with `unity_capture_state`, Console output or a performance snapshot.
5. Clear temporary watches and probes when finished.

All tools in this guide are read-only with respect to project assets. Commands that change scenes, scripts or
assets remain protected by the separate Write gate.
