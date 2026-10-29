# Host runtime

The .NET 10 Generic Host composes the complete Core pipeline.
It does not provision Azure resources, create health models/entities, download
packages, persist reports or promise delivery across restart.

## Commands

With the environment and image-style bundled packages configured:

```powershell
dotnet run --project src\Host -- run
dotnet run --project src\Host -- validate
dotnet run --project src\Host -- probe ready
dotnet run --project src\Host -- probe live
```

No arguments selects `run`. Commands return zero on success, nonzero on
validation/startup/probe failure. Unknown arguments fail without starting work.
Use the built/published DLL rather than `dotnet run` in exec probes.

`validate` loads the same local package catalog and immutable environment
snapshot, using **Deployment** validation (including package auth/query schemas).
It never constructs credentials, resolves secrets, starts telemetry, polls,
checks remote models or publishes. It reports acceptance/rejection plus each
safe configuration variable/property location to stderr on rejection, never
payload values, parser messages or untrusted extension-property names.
Runtime validation deliberately tolerates unavailable plugins;
affected signals report `Unknown`, while deployment validation rejects them.

`validate-packages <packages-root>` checks a nonempty canonical published-package
catalog using the same loader, without requiring plugin/model environment
configuration or constructing sink clients, credentials, telemetry or tokens.
The [package delivery script](image-delivery.md) uses this check before staging.

`probe ready` / `probe live` construct only a bounded loopback HTTP probe,
not the configuration, catalog, worker or auth services. They request
`http://127.0.0.1:8080/healthz/ready` or `/healthz/live`, accept only HTTP 200,
and fail closed on redirects, refusal, timeout or cancellation. The entire
headers-only request is bounded to two seconds; proxies, redirects, cookies
and ambient credentials are disabled.
On HTTP 200 they write exactly `Evaluator probe ready succeeded.` or
`Evaluator probe live succeeded.` to stdout; failed probes never write that
marker. The deployment script requires the ready marker because Azure CLI
container exec does not reliably forward the remote process exit code.

## Startup and ownership

`HostRunner.RunAsync(string[], CancellationToken)` and
`ProbeCommand.ExecuteAsync(string, CancellationToken)` are the command APIs.
`HostDependencies` supplies optional time, logging, credential, secret,
HTTP and exporter boundaries for offline composition; defaults use production
implementations, never test behavior. Its `ProbePort` composition parameter
defaults to 8080; tests allocate a distinct loopback port for each real child
process. This is not a process environment/ACI setting and does not create a
production test-mode bypass. `ProbeCommand` also defaults to 8080.

The environment is copied once. Host options, the package catalog, the complete
snapshot and package/custom-auth/query validation succeed **before** credentials
or Azure telemetry are activated. Then the worker initializes telemetry, one
auth registry, the executor's required sink clients and one scoped `ModelClient`
per model. `PollExecutor.InitializeAsync` prewarms its own client factory/cache;
Host never creates duplicate sink instances. Key Vault construction is deferred
until a configured secret actually needs it.

The scheduler starts only after all required client initialization succeeds.
Callbacks run `PollExecutor -> Evaluator -> ReportBuilder -> Publisher` using a
lookup keyed by the complete model URI/entity/signal tuple. Remote warning
checks run independently and cannot gate readiness. State changes alone emit
per-signal Information logs; a structured startup event is also emitted.
Load summaries and fixed rejection phases are Debug diagnostics, never raw
configuration, exception text or upstream responses.

Kestrel listens **only** on IPv4 `127.0.0.1:8080`. There is no management API.
Readiness stays false until initialization and scheduler activation succeed,
and fails immediately when shutdown starts. Liveness allows two minutes for
initialization, then requires the scheduler's independent one-second supervisor
heartbeat within fifteen seconds. Long polling intervals are not heartbeat
stalls. These windows are runtime health checks, not an ACI startup-probe or
termination-grace claim.

## Telemetry and redaction

The shared `SelfHostedEvaluators` meter exports the complete canonical section
10 instruments. Dimensions are configured model/plugin/package identities and
bounded outcome/state/status values, never signal/entity/query identities.
Core emits `group.tick`, child `poll.batch` and correlated `report.publish`
spans; replaced mailbox reports retain their own originating trace context.

With `APPLICATIONINSIGHTS_CONNECTION_STRING`, the actual Azure Monitor
OpenTelemetry distro is started, with an explicit UAMI `TokenCredential`
selected by `AZURE_CLIENT_ID`. Connection-string-only ingestion and default,
CLI or interactive credential chains are not used. Missing connection string
intentionally selects local console trace/metric/log exporters.
Acquired telemetry tokens join the shared secret-redaction registry before the
credential returns them to the SDK.

Live Metrics, performance counters, standard SDK metrics, offline exporter
storage and automatic cloud metadata/resource detectors are disabled. Host sets
the SDK's process-only `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true` opt-out so
there is no independent Statsbeat exporter outside its authentication/redaction
boundary. Untrusted `OTEL_RESOURCE_ATTRIBUTES` and environment switches that
disable HTTP query redaction are not applied.

All owned loggers preserve only sanitized structured state, suppress raw
exception objects and scrub credentials, registered secrets, headers and URL
queries. Trace redaction runs before exporter processors: display names, tags,
baggage, trace state and status descriptions are sanitized. Unsafe immutable
operation/source metadata, events or links cause the entire span to be dropped,
not exported with sensitive fields or rewritten correlation IDs. HTTP/ASP.NET
raw exception recording is disabled.

Offline tests inject local credentials and HTTP/exporter transports into the
real distro, assert actual authenticated export payloads, and never send to
Azure. Sink/model composition tests likewise use injected transports; no
production certificate-validation bypass is introduced.

## Local process-level verification and operational gaps

`tests\SelfHostedEvaluators.Host.Tests` drives the real `HostRunner` in-process
with an immutable startup environment, an offline fixture package, the actual
auth pipeline, and injected credential/secret/transport/exporter boundaries. It
launches the built Host as a child process only for CLI and probe commands. No
OS-wide trust or production HTTP exception is introduced. These tests do not
restart the process or compare environments across restarts; the snapshot is read
once at start, so verify changed cadence/TTL, new signals and removed-signal
expiry in a test deployment.

Loopback readiness means local composition and scheduling are active, **not**
that credentials, sinks, ingestion, private routing or remote telemetry are
healthy. Warning checks do not gate it. A ready worker can publish Unknown,
retry a throttled model or have no accepted reports. Liveness tests the
scheduler heartbeat, not end-to-end reporting.

Core tests cancel an active partial body through the real authenticated
multi-attempt pipeline, checking `OperationCanceledException` and response/body
disposal before client disposal; that proof does not rely on OS socket cleanup
at process exit.

Host shutdown tests cancel `HostRunner` with a token, not a real signal, and do
not prove caller-cancellation disposal. Forced termination discards
in-memory work; a new process polls immediately rather than replaying it. These
Windows tests alone do not prove Linux ConsoleLifetime SIGTERM handling or the
chiseled image's startup/probes. The separate
[`scripts\test-container.ps1` gate](image-delivery.md#actual-local-container-acceptance)
has now exercised those paths in the actual Linux/amd64 image, including
SIGTERM during an active flushed SSE body with a five-second shutdown budget,
readiness failure and exit code zero without SIGKILL. It also distinguishes
forced termination and verifies liveness failure under a stalled PID 1 followed
by an explicit local restart. It does not prove ACI's automatic restart policy.

Use the authorized readback/runbook in [deployment](deployment.md#authorized-readback)
to check actual model signals after a rollout. A 202 only confirms asynchronous
acceptance, not model recalculation. Monitor publish gaps, errors, budget waits,
superseded/expired/shutdown drops, auth failures, starts and state changes together.
No built-in alert tracks every configured signal, resolves a stuck external
credential, guarantees diagnostic export at forced stop, or prevents duplicate
workers from competing for the same signal names.

## Shutdown

Generic Host ConsoleLifetime handles SIGTERM/cancellation. Readiness fails,
scheduling and warning checks stop, and actual poll callbacks can finish.
Poll draining, report flushing, telemetry flushing and the **wait** for disposal
share one remaining `SHE_SHUTDOWN_TIMEOUT` budget (default five seconds), not
fresh per-stage timeouts.

At deadline the worker cancels outstanding poll waits and best-effort counts
remaining shutdown drops. Process return never waits indefinitely for disposal.
Background ownership retains the scheduler, executor, actual checks/sends,
model clients, auth, catalog and telemetry until underlying calls really finish;
hung operations are never force-disposed underneath their users. Forced process
termination may lose both reports and final diagnostics. There is no guaranteed
delivery, durable queue or configurable ACI termination-grace promise.
