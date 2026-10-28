# Request queue and health traffic

Node/TCP requests that need the Editor now wait in a bounded FIFO queue. The queue holds at most 64 pending operations and starts at most four operations per Editor update, with a cooperative five-millisecond pumping budget. A single running Unity operation cannot be interrupted by that budget. Native/CLI calls already executing on the main thread run directly.

Pending work expires after 20 seconds without starting. A waiting caller receives `QUEUE_TIMEOUT`; pumping the queue later cannot execute the expired action. A full queue returns `QUEUE_FULL`. Domain reload and Editor shutdown cancel pending requests with `EDITOR_RELOADING`. Write permission is checked when queued dispatch actually starts.

If execution has already started and no result is available within the 25-second wait, the response is `EXECUTION_IN_PROGRESS`, with `retryable: false`. Unity may still finish that operation. Inspect the affected state before submitting it again; this version does not provide a job-status handle for synchronous operations.

The Node bridge's ordinary transport timeout is 30 seconds, above the queue/wait budgets. It retries only explicitly listed safe reads, at most twice. Mutations and unknown commands are never automatically replayed. If their transport response is lost, `EXECUTION_OUTCOME_UNKNOWN` means that the operation may already have executed. Existing command-specific longer transport deadlines remain compatible, but do not extend the server's synchronous result-wait budget.

Consecutive successful pings from the same source appear as one Activity row with a repeat count and latest timestamp. Errors, different sources and intervening commands remain separate. Ping reports transport liveness, not main-thread readiness; grouping does not reduce the caller's ping frequency.

This improves bounded concurrency within the existing shared Editor. The global rate limit and per-Editor write permission remain shared. It does not introduce chat identities, independent game Players or per-session isolation. Presence still depends on Editor updates, so a long Editor stall can make discovery report a stale heartbeat; an original disconnect incident still needs correlated diagnostics.
