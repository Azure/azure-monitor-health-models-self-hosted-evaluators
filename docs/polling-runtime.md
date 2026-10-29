# Polling and evaluation runtime

Core supplies polling/evaluation components and
[report construction and publishing](reporting-runtime.md), composed by
[Host orchestration](host-runtime.md). All components consume the validated startup
`ConfigSnapshot`; they do not reread the environment.

## Composition

Share one `TimeProvider`, `Redactor` and
`SelfHostedEvaluators.Core.Diagnostics.RuntimeDiagnostics` across the pipeline.
Construct:

- `Polling.Scheduler(time, diagnostics)`.
- `Polling.PollExecutor(snapshot, pluginManager, time, diagnostics, logger)`. The
  alternative constructor accepts a client factory returning an **owned**
  `ISinkClient`, for offline composition/tests. It must not return one client
  instance for multiple plugin entries.
- `Evaluation.Evaluator(redactor, time, diagnostics)`.

`Scheduler.RunAsync(snapshot, onTick, ct)` calls
`Func<ScheduledBatch, CancellationToken, Task>` once per split batch.
`ScheduledBatch` contains `PluginName`, `Window` (`QueryWindow`) and immutable
`Queries`. The callback should await `executor.ExecuteAsync(batch, ct)`,
evaluate each returned result against its configuration using the **full**
`SignalKey` (model URI, entity, signal name), and enqueue the later report.
Keep a lookup by full key; signal names alone are not unique across models.
Before scheduling, Host calls `executor.InitializeAsync(pluginNames, ct)` for
available configured packages. This prewarms the executor's existing cache:
initialization and polling use the **same owned client**, not a second set
created by Host. Missing runtime plugins remain `Unknown`.

## Scheduling and execution

Signals are grouped across models by ordinal plugin name and effective interval,
then split at the validated plugin batch cap. An absent plugin entry uses
one-signal batches so those signals can still report `Unknown`.

Each group has one immediate startup poll and subsequent UTC Unix-epoch-aligned
ticks. Its stable offset is FNV-1a-64 of UTF-8
`pluginName + ":" + interval.Ticks` (invariant decimal), modulo interval ticks.
This does not use randomized process hash codes or drift with startup time.
Overlapping ticks and elapsed ticks missed during a delayed wake are counted
and skipped, not replayed. Callback exceptions are observed and do not kill
future schedules. The callback invocation itself runs off the scheduling loop,
including any synchronous code before it returns a task.
`HasRecentHeartbeat(window)` observes a one-second supervisor inside the
scheduler, independent of group intervals. A normal day-long interval does not
look stalled; a completed/stalled scheduler does not remain live merely because
an unrelated health timer runs.

The executor lazily activates at most one client per plugin entry. Completed
failed/canceled activation attempts (including exceptions and null clients)
may be retried by later polls, unless the plugin has been quarantined. The
activation task is shared across concurrent groups; a running task or successful
client is never replaced. The factory must finish cleaning up a failed attempt's
resources before completing its task, as `PluginManager` does. A separate
plugin-wide semaphore limits batches across **all** models and interval groups.
The external budget is `min(plugin.Timeout, batch.Window.Interval)`, including
the concurrency queue and activation wait. Every expected key gets exactly one
result in input order. Missing, duplicate, malformed, foreign-only and
nonfinite observations fail; valid results for other keys remain intact.
Exceptions, timeout, cancellation and unavailable plugins become per-signal
failures, never raw plugin exceptions. Runtime validation permits a missing
package; deployment validation still rejects it. Invalid settings for an
available package remain configuration errors.

**No request permit is charged at batch entry.** Endpoint/auth clients already
charge every outbound attempt and apply per-plugin `Retry-After` pauses.
Non-HTTP plugins must follow the request-lease contract in `auth-runtime.md`.
The executor does not add retries around entire batches.

## Cancellation, quarantine and ownership

External timeout/cancellation returns failure results promptly and requests
underlying cancellation asynchronously. It does **not** release the batch
permit or dispose resources before the actual plugin task finishes. This
includes synchronous blocking invocation, client activation and
`SingleQueryClientBase`'s real worker lifetime. Eventual failures are observed.
Plugin cancellation callbacks are also observed before cleanup completes.

An active call still unfinished at external timeout/cancellation temporarily
blocks that plugin entry. Subsequent ticks return `plugin_quarantined` without
activating a replacement or invoking that client; other plugins continue.
Cancellation gets a fixed five-second asynchronous grace period, without
delaying the original timeout result. Work still unfinished after grace is
counted as a hang; quarantine entry and recovery emit safe Warning logs.
Recovery occurs only after **all** outstanding cancelled calls and their
cancellation callbacks actually finish. A permit stays held through that
cleanup, even if the plugin task finishes before its callbacks. Queued-only
timeouts do not quarantine the plugin. These protections
bound retained invocations, not the behavior of arbitrary trusted in-process
code; there is no process sandbox.

A permanently hung call cannot be killed in-process: its plugin entry remains
blocked and resources remain owned until operator restart/redeployment terminates
the process. No automatic restart or new liveness policy is implemented.
The independent scheduler heartbeat stays live; accepted `Unknown` reports can
keep the accepted-report-gap alert quiet. Operators should monitor
`evaluator.poll.hangs` / `evaluator.poll.quarantines` and the Warning transitions;
the shipped optional alerts cover accepted-report gaps and restart loops only.

Shutdown composition:

1. Cancel the token passed to `RunAsync`, and await it to stop schedule loops.
   This does not wait indefinitely for active callbacks.
2. Await `scheduler.DrainAsync(budgetToken)` and
   `executor.DrainAsync(budgetToken)` for best-effort poll completion.
3. Call `publisher.FlushAsync(budgetToken)` with that same remaining
   host shutdown budget, after poll callbacks can no longer enqueue.
4. Call `DisposeAsync` on scheduler/executor. Executor disposal stops new work,
   cancels outstanding waits, then waits for actual task/cancellation lifetimes
   before disposing clients. It is idempotent and may remain pending forever
   for an uncooperative plugin. Bound the **wait** on this task at the host;
   never force-dispose the client's context or package catalog underneath it.
   A canceled drain can be retried later. Drain snapshots are intended after
   scheduling has stopped, not as a barrier against future submissions.

The host must keep the plugin manager/catalog, auth services and shared
diagnostics alive through actual executor disposal. Do not independently
dispose clients owned by the executor.

## Evaluation and TTL

`Evaluator.Evaluate(key, configuration, result)` returns `EvaluatedSignal`:
key, `Configuration.HealthState`, finite optional value, optional
`EvaluationRules` (`UnhealthyRule`, `DegradedRule`), effective
`ExpiresInMinutes`, and optional redacted context.

Thresholds test unhealthy before degraded; all six operators are supported.
State maps ignore case; unmapped states are `Unknown`. `NoData` uses its
configured state (default `Unknown`). Missing required numeric/native input,
wrong keys and nonfinite observations are failures, regardless of `noData`.
Failures always produce `Unknown` with `Poll failed: {code}: {message}`,
ignoring the success template. Success defaults to detail/no-data reason.
Templates substitute the five documented variables in one pass, use invariant
numeric formatting and round-trip ISO-8601 observation time, then redact the
entire text **before** truncation to 4096 UTF-16 characters (without splitting
a surrogate pair).

`TtlCalculator.Calculate(interval, multiplier, overrideMinutes)` has integer
and double multiplier overloads. It parses the double multiplier's invariant
round-trip decimal representation before tick-first arithmetic and ceiling:
`3.0000000000000004` at two minutes yields 7, while `2.1` at 200 seconds stays
exactly 7. Calculated TTL is clamped to 1..10080 (including extreme finite
multipliers); explicit overrides must be within that range. Exact ticks enforce
at least twice the interval, including when a clamped TTL is insufficient.
The environment parser calls this same helper, retaining property-path-safe
configuration errors. Validated signals already carry their effective TTL;
evaluation preserves it rather than recomputing with different defaults.

## Diagnostics

`RuntimeDiagnostics.MeterName` is `SelfHostedEvaluators`; an injectable
instance owns a public `Meter`. A process-wide `Shared` instance is the default.
Subscribe/export before polling. Instrument names:

- Canonical section 10 instruments: `evaluator.polls` (outcome:
  success/failure/timeout/cancelled), `evaluator.poll.duration` (ms),
  `evaluator.evaluations` (health state), `evaluator.schedule.lag` (ms),
  `evaluator.ticks.skipped`.
- Supporting scheduler instruments: `evaluator.ticks`,
  `evaluator.schedule.callback_failures`.
- Supporting poll instruments: `evaluator.poll.hangs`,
  `evaluator.poll.quarantines`, `evaluator.poll.inflight` (actual active
  call/activation lifetime), `evaluator.poll.underlying_duration` (ms).
- Supporting evaluation instrument: `evaluator.evaluation.duration` (ms).

Only configured plugin name, fixed outcome and health-state tags are used;
no model/entity/signal tags, query text, payloads, exception messages or
credential data. A hang means work was unfinished after the cancellation grace,
not proof that it will never finish.
Host extends this meter with config/package loads, active signals and host
starts; reporting uses it too. Sink and model permit waits share
`evaluator.ratelimit.wait` with plugin/model tags respectively. No aliases or
separate subsystem-specific meter are emitted.
`SelfHostedEvaluators.Core.Polling` emits `group.tick` and child `poll.batch`
spans. Tick spans include actual batch callback time; callbacks retain their
batch context through evaluation/report enqueueing.
