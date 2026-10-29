# Reporting runtime

The `SelfHostedEvaluators.Core.Reporting` library does not
start Host, create models/entities, provision resources or persist reports.

## Host composition

`Host.RuntimeWorker` implements this composition; see [host runtime](host-runtime.md).
After validating the entire startup snapshot/catalog, share the existing
`AuthRegistry`, `Redactor`, `TimeProvider` and `RuntimeDiagnostics`:

1. For **each** configured model, await
   `ModelClient.CreateAsync(model.Name, model.HealthModel, registry, time,
   redactor, diagnostics, logger, ct)`. Keep these clients for the deployment
   lifetime. Each gets its own auth context/credential with its configured token
   scope, its own request budget and its own 429 pause.
2. Construct `ReportBuilder(time, redactor)` and
   `Publisher(modelClients, time, redactor, diagnostics, logger)`.
3. Start `publisher.RunAsync(workerToken)` and
   `new ModelChecks(modelClients, time, redactor, diagnostics, logger)
   .RunAsync(snapshot, checksToken)` independently of scheduling.
   Checks warn only; do **not** await their completion as a readiness gate.
   Host offloads their invocation too, so synchronous auth/transport work
   cannot block scheduler activation.
4. For each evaluated poll result, call `builder.Build(evaluatedSignal)`, then
   await `publisher.EnqueueAsync(report, callbackToken)`. Preserve the full
   `SignalKey` (model URI, entity, signal name). The builder consumes the
   evaluator's already-resolved TTL, including fractional multiplier results.
   `Build(signal, interval, int ttlMultiplier, int? expiresInMinutes)` is also
   available for callers that explicitly need `TtlCalculator` derivation.

`HealthReport` holds the full key, buffered UTF-8 `ReadOnlyMemory<byte> Body`,
`CreatedAt`, `ExpiresAt` and a generated `ClientRequestId`. Treat the read-only
buffer as immutable. Every attempt has the same body/client ID and uses
`x-ms-return-client-request-id: true`. Entity addressing is in the route, not
the body; there are no body entity/model IDs. Requests have byte-accurate
`Content-Length`, JSON UTF-8 content type and no chunked transfer encoding.
Report validation enforces the exact section 8 name pattern, finite numbers,
defined health states/operators, a required unhealthy rule when rules are
present, TTL 1..10080, context redaction **before** truncation to 4096 UTF-16
characters (without cutting a surrogate pair), and a final 32768-byte limit.

## Single-attempt transport and request accounting

`AuthRegistry.CreateSingleAttemptClientAsync(profile, endpoint, timeout, ct,
azureScope?)` is an owning authenticated HTTP client. Unlike `CreateClientAsync`
(the unchanged sink pipeline), it makes **one** data-plane attempt, applies
no sink permit, and never performs hidden HTTP retries or shared sink pauses.
Redirect following, cookies and ambient credentials remain disabled. 3xx
responses are returned for the publisher to reject, not followed. Model requests
use `ResponseHeadersRead`: checks inspect status and apply a 429 model pause
before touching content, then buffer only successful entity bodies with a
64 KiB bound and the original request timeout. Unneeded GET error bodies are
disposed unread, so unreadable/oversized 429 content cannot erase the shared pause.
The publisher sets Core's
internal discard-response-body option on report requests: status/correlation
headers are authoritative and unneeded report response content is disposed
without reading it. A broken/oversized error body must not turn 202 acceptance
or a permanent rejection into a retry. Auth/identity exchanges retain their
separate, owned 60/minute budget and existing bounded exchange retries; they
do not consume model data-plane permits.

`ModelClient.SendAsync(request, ct)` acquires exactly one model permit for each
attempt, including retries and remote GET checks, then uses that transport.
Its continuously replenished token bucket starts with the configured
`reportsPerMinute` capacity and replenishes at that rate per minute.
Do **not** add permits/retries in Host or use the sink `CreateClientAsync`
inside this wrapper. The public constructor accepts an **owned** single-attempt
`HttpClient` for offline/recording-handler composition; callers must not inject
their own retrying handler. Use `CreateAsync` in production.

The publisher retries 408/429/500..599, network failures, request timeouts and
transient auth-operation failures until 202, replacement or TTL. Each nonretryable
status (including 400/401/403/404/413/415, redirects and unexpected 2xx) is explicitly
logged and dropped. Both Retry-After delta seconds and HTTP dates are supported;
past dates mean zero delay. Absent/malformed headers use exponential jittered
backoff capped at 30 seconds. Malformed headers produce an explicit warning
without recording the raw value. 429 applies its delay to the **model**, including
other signals and GETs; replacing a report never clears that shared pause.

Each signal has a latest-only mailbox and at most **one actual send**.
Replacement cancels unaccepted work and its retry/permit waits, but does not
detach its underlying request task. Even a handler that ignores cancellation
must finish before that signal can send the replacement. Intermediate unsent
reports are superseded, not accumulated. Expiry timers count/remove expired
latest reports even when an older underlying send is still stuck. Absolute
`TimeProvider` deadlines are also checked after waits and immediately before
dispatch, including model permit, authentication and transport-configuration
waits; expiry does not depend on which timer callback runs first. Retry and
permit waits are clipped to the report's remaining lifetime. Enqueue uses a
coherent nonnegative timer duration and rechecks expiry before starting work.
No disk queue,
replay, cross-restart durability or artificial acceptance guarantee is provided.

## Remote checks

Checks send authenticated `GET /api?api-version=...` and one
`GET /api/entities/{entity}?api-version=...` per distinct configured entity,
using the same scoped clients and budgets as publishing. Each model checks
immediately and has an independent 30-minute periodic cadence; slow/paused
models cannot stall another model's checks. Overrun ticks coalesce, not overlap.
Failed status/auth/transport/body checks warn only and do not change validation,
evaluation, readiness or publishing. No resources are created.

Entity JSON is bounded/depth-limited and inspected only after object/type checks.
The current diagnostic contract's root `signalInputs` array is counted for both
`2026-06-01-preview` and `2026-10-01-preview`. If that property is absent, legacy
`signals` arrays at the root or under `properties` remain supported. A present
but malformed `signalInputs` warns without falling back to legacy fields.
At 50 or more inputs the check emits an advisory `signal_cap` warning; this
projection does not distinguish external signals from other input kinds, so the
count does not establish external-signal capacity. Unknown/malformed shapes warn
rather than assuming an empty entity. Response bodies and parse/transport
exception messages are not logged. The checks intentionally do not retry
immediately; a 429 still applies the model pause before subsequent calls.

## Shared-budget shutdown and resource ownership

1. Stop scheduling and remote checks. Canceling/awaiting `Publisher.RunAsync`
   only stops its service wait; it does **not** abandon pending work. Finish
   poll callbacks first (or prevent their late enqueues with cancellation).
2. Pass the **same remaining Host shutdown budget token** used for poll draining
   into `publisher.FlushAsync(budgetToken)`. This permanently closes enqueueing
   and best-effort drains mailboxes, even if `RunAsync` was never started.
   Do not create a fresh per-publisher five-second budget.
3. At budget cancellation, flush counts/drops remaining latest reports, requests
   cancellation and returns without waiting indefinitely for an uncooperative
   send. Already-expired/permanent/superseded reports are not counted again.
4. `Publisher.Completion` observes actual workers/cancellation cleanup after
   enqueueing stops. Keep auth/redaction/diagnostics alive through completion
   and stop/observe the checks task. `ModelClient.Dispose()` closes new calls
   and **defers** HTTP/auth disposal while its actual requests or returned response
   contents are still active. Always dispose returned responses/contents, including
   unread bodies and body-error paths. Streamed single-attempt responses retain
   their transport generation, timeout and certificate/auth context until content
   disposal, including across rotation or client disposal.
   A permanently stuck transport can keep completion pending until process exit;
   never force-dispose resources underneath that task.

## Diagnostics

Reporting extends the existing `SelfHostedEvaluators` meter, not a second meter:

- `evaluator.publishes`: HTTP status or fixed network/timeout/auth-failure status.
- `evaluator.reports.superseded`: replaced, unaccepted reports.
- `evaluator.reports.dropped`: fixed `expired`, `permanent`, `shutdown` or
  unexpected `failure` outcome.
- `evaluator.ratelimit.wait`: model request budget/pause wait in milliseconds.
- `evaluator.checks.warnings`: fixed HTTP/shape/transport/signal-cap outcome.

These instruments use configured model identity and bounded status/outcome
dimensions, never signal/entity/query/body/URI dimensions.
`SelfHostedEvaluators.Core.Reporting` emits `report.publish` client spans with
the stable `client.request.id`, redacted/bounded `service.request.id` from
`x-ms-request-id`, model identity and HTTP status. It never records report bodies
or raw headers. Each mailbox work item retains its enqueueing batch's trace
context, including replacements; a long-lived mailbox does not incorrectly
parent later publishes to its first batch. Host wires exporter redaction and
the remaining canonical metrics on the same meter.

All reporting tests use injected recording handlers, fake credentials and
`FakeTimeProvider`; no live identity, Key Vault, sink or model is contacted.
