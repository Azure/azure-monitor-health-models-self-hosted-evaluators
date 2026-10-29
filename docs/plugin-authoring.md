# Authoring and shipping sink packages

Use the stable [v1 plugin contract](plugin-contract.md) and .NET **10.0**
(`global.json` pins the SDK). Production packages reference
`Plugins.Abstractions` and, if needed, public Extensions abstractions only:
never Core, Host, Publisher, Azure SDKs or another sink. The first-party
packages link `Plugins.Common\PluginJson.cs` as **private source**, not a new
shared contract assembly. `Plugins.Testing` is a test-only dependency; do not
include NUnit or WireMock in a sink's publish output.

Production projects generate XML documentation, and the existing
warnings-as-errors policy rejects undocumented public API members. Document
public types, constructors, methods, properties, and enum values with XML
summaries; include parameter, return, cancellation, and resource-ownership
details where they define the contract. Use `inheritdoc` for implementations
of an already documented interface or base member rather than duplicating it.

## Package structure and activation

1. Implement one public `IPluginPackage` with a parameterless constructor.
   Its `Register` calls `SetSinkPlugin<T>()` once. Implement one `ISinkPlugin`
   with strict `ValidateSettings` / `ValidateQuery` methods and capabilities.
   Return generic/redacted validation errors, not user JSON.
2. `CreateClientAsync` creates a **new** `ISinkClient` for every environment
   entry. Check cancellation before activation. Derive single-query clients
   from `SingleQueryClientBase` for bounded batch fan-out and exception mapping.
3. Return one result per input, retaining the entire
   `SignalKey(ModelUri, Entity, Name)`, including repeated keys. An empty batch
   returns an empty list; pre-canceled and active caller cancellation propagate.
   Do not substitute a successful observation for an error, empty response,
   unsupported API or truncated stream.
4. Include `plugin.json`, with the actual DLL and fully-qualified entry type:

   ```json
   {
     "id": "datadog",
     "version": "1.0.0",
     "assembly": "SelfHostedEvaluators.Plugins.Datadog.dll",
     "entryType": "SelfHostedEvaluators.Plugins.Datadog.DatadogPackage",
     "abstractionsVersion": "1.0.0"
   }
   ```

   Set `CopyToOutputDirectory` and `CopyToPublishDirectory` to
   `PreserveNewest`. Use the same `<id>/<version>` directory names as the
   manifest. Core already checks manifest/version constraints and rejects an
   incompatible abstractions major. Framework, abstractions, Extensions
   abstractions and RateLimiting have host-shared identity; private
   dependencies stay inside the package's AssemblyLoadContext. Do not modify
   that stable loader policy.

The supplied implementations cap batches at 50 (Datadog/Splunk/Azure Monitor) or 100
(Static); each batch fans out at most `min(effectiveBatchSize, 8)` queries.
Core enforces the configured shared concurrency, rate limits, batch timeout,
bounded HTTP retries and failure-to-Unknown evaluation.

## Credential and endpoint boundary

All named `HttpClient`s come from `PluginInstanceContext.HttpClients` and
are already authenticated, destination-guarded, rate-limited and retried.
Sinks neither resolve credentials nor call `context.Auth` for HTTP.
Do not set authentication headers, create replacement clients or dispose
context-owned clients/auth/limiters. Dispose only sink-owned responses,
requests and streams. Only non-HTTP transports use the public auth operations
and acquire a rate-limiter lease themselves.

First-party HTTP endpoint values are **root HTTPS origins**, with no path,
query or fragment; the sink appends the documented API paths. No host/site is
hardcoded in the sink. There is no query-supplied URL or auth override.

| Package id | Required endpoint names | Examples |
| --- | --- | --- |
| `datadog` | `default` | `https://api.datadoghq.eu`, `https://api.datadoghq.com` |
| `splunk-observability` | `api`, `stream` | `https://api.eu0.signalfx.com`, `https://stream.eu0.signalfx.com` |
| `azure-monitor` | `default` | The workspace's query endpoint, such as `https://workspace-name.region.prometheus.monitor.azure.com` |
| `static` | none consumed | Core still requires a nonempty map, for example `{"default":"https://sink.test"}`; auth `{"type":"none"}`; no request is made |

Splunk also documents `api.<realm>.observability.splunkcloud.com` and
`stream.<realm>.observability.splunkcloud.com`. Use your organization's realm
and access-token permissions. Configure `X-SF-Token` **in Core's apiKey auth
profile**, not settings or queries. Datadog read APIs require the matching
site's API/application keys, configured as `DD-API-KEY` /
`DD-APPLICATION-KEY` in that profile.

Azure Monitor uses Core's `entraId` profile with
`scope:"https://prometheus.monitor.azure.com/.default"` and a nested Azure
credential profile. Grant that identity **Monitoring Data Reader** on the
existing workspace. No Azure SDK or token acquisition belongs in the plugin.

## Common query rules

Schemas are case-sensitive closed objects: unknown/duplicate properties,
wrong kinds, empty/whitespace strings and control characters are rejected.
All durations are positive integer strings with `s`, `m`, `h`, or `d`,
bounded to one day (examples: `10s`, `5m`, `2h`, `1d`). Decimal durations,
zero, negatives and null are invalid. Query strings are at most 4096
characters unless a lower bound is specified below.

Datadog and Splunk metric queries accept optional `aggregation`: `last` (default), `mean`,
`min`, `max`, `sum`. This is a **client-side reduction over all finite,
non-null samples across all returned series in the requested window**.
Vendor expressions may themselves perform vendor-side aggregation.
`last` selects the newest timestamp; equal timestamps select the greatest
value, independently of series/response order. Other reductions use all
samples and timestamp the result with the latest included sample.
Out-of-window samples are ignored; malformed and non-finite samples fail
the query rather than being silently discarded. Null values are missing
data. Empty/all-null windows return NoData. Overflowing reductions fail.

An omitted `window` uses `QueryWindow.Interval`. Bounds are based on
`QueryWindow.ScheduledAt`, **not wall-clock request time**. Samples on either
boundary are included; a vendor may return fewer points at its own exclusive
stop boundary.

Azure Monitor instead executes instant queries; lookback and aggregation are
expressed in PromQL, as described below.

### Azure Monitor settings and queries

Settings are exactly `{}`. Queries contain only `promql`:

```json
{"promql":"sum(rate(http_requests_total{job=\"checkout\"}[5m]))"}
```

Use the existing workspace's **Query endpoint** from its Overview page.
The `default` authenticated HTTP client sends:

```text
POST /api/v1/query
Content-Type: application/x-www-form-urlencoded

query=<form-encoded PromQL>&time=<scheduled Unix-seconds>
```

The evaluation time is `QueryWindow.ScheduledAt`, including fractional seconds,
not the request's wall clock. No query string, URL, authentication override,
`window`, or client-side `aggregation` option is accepted. Expressions are
bounded to 4096 characters, without control characters; Azure validates PromQL
syntax and service-specific restrictions.

Require `status:"success"` with `data.resultType` of:

- `scalar`: `data.result` is `[Unix-seconds, "numeric-value"]`.
- `vector`: `data.result` is an array containing exactly one object with string
  labels in `metric` and a `value` pair in the same format. An empty array is
  NoData. Multiple series fail with `multiple_series`; use PromQL aggregation
  or label selection instead of silently choosing or combining series.

Parse numeric strings with invariant culture, including signed/exponent values.
Preserve the returned fractional sample timestamp as `ObservedAt`; invalid,
out-of-range, or future timestamps fail. Non-finite values (`NaN`, infinities,
overflow), nulls, malformed pairs, string/matrix results, and native histograms
fail with `invalid_response`, not NoData or an invented native state.
Nonempty `warnings` or `infos` fail with `query_annotation`, conservatively
rejecting potentially incomplete results; annotation text is not echoed.
HTTP failures retain safe status codes and Retry-After through the common
helper. The host handles authentication, timeouts, rate limiting and retries.

This package reads managed Prometheus metrics in an Azure Monitor workspace;
it does not query Log Analytics workspaces or execute KQL. See the complete
[configuration example](configuration.md#azure-monitor-workspace-managed-prometheus)
and [published-package workflow](image-delivery.md#azure-monitor-package).

### Datadog settings and queries

Settings are exactly `{}`. Query is **one of**:

```json
{"metric":"p95:trace.http.request{service:checkout}","window":"5m"}
```

```json
{"metric":"avg:system.cpu.user{env:prod}","aggregation":"max"}
```

```json
{"monitorId":12345678}
```

- `metric`: required nonempty Datadog expression. Optional `window` and
  `aggregation` only. Sent URL-encoded to
  `GET /api/v1/query?from=<Unix-seconds>&to=<Unix-seconds>&query=<expression>`.
  Parse `status:"ok"` and `series[].pointlist` pairs
  `[Unix-milliseconds, number-or-null]`. The v1 API is intentionally used
  for the approved expression examples; Datadog currently recommends v2 for
  new timeseries consumers, but v1 remains documented.
- `monitorId`: required positive signed 64-bit JSON integer. No other
  fields permitted. `GET /api/v1/monitor/{monitorId}` must return that ID.
  Preserve `overall_state` as native state: `OK`, `Warn`, `Alert`, `No Data`,
  `Ignored`, `Skipped`, `Unknown`. A null state is NoData; missing/unrecognized
  state or wrong ID is failure. Observation time uses the injected clock at
  response processing: the monitor's `modified` is configuration time, not
  measurement time. No arbitrary monitor message/response body is copied
  into results. Map vendor states using the model's `evaluation.stateMap`,
  as in spec section 6 (for example `Warn` to `Degraded`).

### Splunk Observability settings and queries

Settings are exactly `{}`. Query is **one of**:

```json
{"metric":"cpu.utilization","window":"5m","resolution":"10s","aggregation":"mean"}
```

```json
{"detectorId":"AbC_123-","detectLabel":"loadWarning"}
```

**Metric** fields are `metric` (required), `window`, `resolution` and
`aggregation` (optional). `metric` is a metric selector string passed as an
escaped SignalFlow string literal, **not arbitrary program text**. No
`programText`, JSON program, credential or URL fields are accepted.
The request uses named **stream**:

```text
POST /v2/signalflow/execute?start=<Unix-ms>&stop=<Unix-ms>[&resolution=<ms>]
Content-Type: text/plain; charset=utf-8
Accept: text/event-stream

data('cpu.utilization').publish()
```

The positive bounded `stop` makes this a finite historical computation, not
a persistent subscription. Resolution is omitted when unspecified so the
vendor chooses it. The response is SSE, **not a JSON timeseries REST
response**. Parse comments, IDs, multiline `data:` fields and these documented
message types:

```text
event: data
data: {"logicalTimestampMs":1699999990000,
data: "data":[{"tsId":"CgrT2EkAAAA","value":42}]}

event: control-message
data: {"event":"END_OF_CHANNEL","timestampMs":1700000000000}

```

`metadata`, `message`, `event` and `expired-tsid` do not contribute metric
values, but must still have valid envelopes. Metadata requires a nonempty
`tsId` and object `properties`; expired-tsid requires `tsId`; message requires
integer `logicalTimestampMs` and object `message`; event requires `tsId`,
object `properties`, integer `timestampMs` and object `metadata`.
Arbitrary JSON in an ignored event is not accepted.
Startup/progress control messages are recognized.
`error` and `CHANNEL_ABORT` are failures, including after numeric data.
An invalid envelope, unexpected event/control name, non-SSE response,
non-finite value, or EOF before `END_OF_CHANNEL` is a failure.
Return results only after normal termination, including NoData if there
were no in-window numeric samples. Streams exceeding 8 Mi characters fail
explicitly. Engine cancellation covers the entire stream, including body
reads after response headers.

The real authenticated HTTP pipeline also bounds body buffering by the effective
request timeout. Core tests prove caller-cancellation response/body disposal
through the authenticated multi-attempt pipeline, before client disposal.
Process-shutdown/forced-stop connection closure is not that proof, and none of
these Windows tests alone verifies Linux SIGTERM. The separate
[real Linux/amd64 container gate](image-delivery.md#actual-local-container-acceptance)
now exercises SIGTERM with a flushed partial SSE response and the actual
production image. Delaying response headers alone does not test active-body
cancellation.

**Detector** fields are `detectorId` (required, 1–256 ASCII letters/digits/
underscore/hyphen) and optional `detectLabel` (1–256 characters). This is
a **current active-incident snapshot**, not a metric/window query.
It uses named **api**:
`GET /v2/detector/{id}/incidents?offset=0&limit=100`.
The documented response is a bare array, not a `results` envelope:

```json
[
  {
    "active": true,
    "anomalyState": "ANOMALOUS",
    "detectLabel": "loadWarning",
    "detectorId": "AbC_123-",
    "events": [{"timestamp":1699999990000}]
  }
]
```

Vendor responses include more fields; only required consumed fields are
shown. Validate detector identity and native `anomalyState`, filter inactive
incidents and optional `detectLabel`, and preserve one of `ANOMALOUS`, `OK`,
`MANUALLY_RESOLVED`, `STOPPED`. Use the latest event timestamp (Unix ms), or
the injected response-processing clock if the incident has no events.
Fetch subsequent pages before deciding, up to 10,000 rows; exceeding that
bound fails rather than returning a partial snapshot.
No matching active incidents means **NoData, never an invented OK/CLEAR**.
Conflicting states fail with `ambiguous_state`; use `detectLabel` to narrow.
There is **no invented `/detector/{id}/status` endpoint**. In particular,
do not assume an empty active-incidents list proves that an enabled,
evaluating detector is healthy. Map native states explicitly in model
configuration; choose the meaning of NoData using model policy.

### Static settings and queries

Settings: `{}` or `{"delay":"1s"}` (optional duration using the common rules).
Delay uses the injected `TimeProvider` and honors cancellation; it does not
make network calls. Queries are these disjoint shapes:

```json
{"kind":"observation","value":42,"nativeState":"OK","detail":"demo fixture"}
```

```json
{"kind":"noData","reason":"empty fixture"}
```

```json
{"kind":"failure","code":"fixture_failure","message":"intentional failure","retryAfter":"7s"}
```

An observation requires finite numeric `value`, nonempty `nativeState`, or
both; `detail` is optional. Explicit nulls are invalid. Native state and
failure code are at most 128 characters; other text fields at most 4096.
NoData requires `reason`; failure requires `code` and `message`, with an
optional positive `retryAfter`. Results retain configured text verbatim,
so **do not put credentials or sensitive content in fixture text**.
Observation timestamps always equal the scheduled tick. Identical query
and window produce identical results. No randomness, sequence counters,
wall-clock observations or remote data are used.

## Failure semantics

HTTP non-success returns `http_<status>`, including 401/403, 429 and 5xx,
with parsed `Retry-After` delta/date when present. Authentication and retries
belong to Core's pipeline; sink code adds no retry loop. Bodies, URLs,
exception messages and credentials are not reflected in failures.
Malformed vendor bodies return `invalid_response`; stream abort/error,
truncation, ambiguity and size/pagination bounds have explicit codes.
`SingleQueryClientBase` maps independent timeout/transport cancellation to
`timeout`, other exceptions to `query_failed`, and propagates caller
cancellation. Core reports polling failures as Unknown immediately.

## Test helpers and local validation

Reference `Plugins.Testing` from the **test project**, alongside the real
package project. Inherit `SinkClientContractFixture` and implement
`CreateScenarioAsync()` returning a `SinkClientScenario` that:

- exposes the real package's `ISinkClient`, not a fake replacement client;
- creates queries with the supplied full key for each `ContractOutcome`;
- drives a fake sink to emit numeric observation `42`, NoData, failure with
  `RetryAfter=7s`, independent timeout, and a genuinely pending delayed query;
- implements `WaitForRequestAsync` with a request-start signal and owns its
  fake transport cleanup.

The inherited NUnit tests assert mixed batching, one keyed result per input
(same names across models/entities and repeated full keys), empty and
pre-canceled batches, active cancellation, timeout mapping and retry data.
Static uses its real deterministic client and an injected TimeProvider:
timer creation signals active work, and a timer-provider TimeoutException
tests base timeout mapping. It is not a fake HTTP success/no-op test.
The WireMock-backed vendor scenarios exercise actual HTTPS clients.
`PluginTestContext` trusts only loopback certificates on its own test
clients; never copy that trust callback into production or change OS trust.
`AssertPackage` additionally checks first-party manifests, registration,
declared endpoints and the production assembly dependency boundary.

Add vendor-specific assertions for auth headers/site isolation, exact
methods/paths/query encoding/body, windows/timestamps/aggregation, NoData,
malformed/non-finite data, native states, and 429/5xx.
Run the same tests before implementation to capture RED, then GREEN:

```powershell
dotnet test SelfHostedEvaluators.slnx --filter "FullyQualifiedName~Datadog|FullyQualifiedName~SplunkObservability|FullyQualifiedName~AzureMonitor|FullyQualifiedName~Static|FullyQualifiedName~SinkClientContract"
dotnet build SelfHostedEvaluators.slnx --no-restore
```

The `scripts\validate-package.ps1` workflow checks a user-built published
package with the actual loader before staging immutable bytes.
See [image delivery](image-delivery.md) for the exact API and trust requirements.
The NUnit/build commands above remain useful during development. Delivery adds
actual isolated published-package loading before including selected packages in
the image.

## Publishing and image inclusion

Publish packages to **separate** directories. For example, from Windows:

```powershell
dotnet publish src\Plugins.Datadog -c Release -o artifacts\packages\datadog\1.0.0
dotnet publish src\Plugins.SplunkObservability -c Release -o artifacts\packages\splunk-observability\1.0.0
dotnet publish src\Plugins.AzureMonitor -c Release -o artifacts\packages\azure-monitor\1.0.0
dotnet publish src\Plugins.Static -c Release -o artifacts\packages\static\1.0.0
```

Each directory must contain its manifest, assembly, deps metadata and any
private dependencies. In the Linux image, copy these publish outputs to
`/app/packages/datadog/1.0.0/`,
`/app/packages/splunk-observability/1.0.0/`,
`/app/packages/azure-monitor/1.0.0/`, and
`/app/packages/static/1.0.0/`. These are container paths, not Windows
filesystem paths. Include third-party packages the same way, rebuild the
image and redeploy configuration; no Core source edit, runtime download,
storage account or remote configuration fallback is needed.
These publish commands alone do not build/deploy an image or call a live
sink/model. Continue with the implemented
**`validate-package` → `build-image` → `validate-config` → `deploy`** scripts.
The first gate runs the real loader against the published package.
The selected-package image is rebuilt for package updates; configuration-only
updates reuse the registry manifest digest. Retain previous package bytes,
digest and complete configuration for rollback. An ALC is per **package**, not
per configured sink: two Datadog entries do not require duplicating that package
in the image. Different instances must never share mutable auth/client state.

## Multiple named sinks

Two external sinks require two independent `SHE_PLUGIN_<KEY>` JSON values.
For example (PowerShell here-strings), both use the same published Datadog
package but distinct names, endpoints, auth references and runtime clients:

```powershell
$env:SHE_PLUGIN_DATADOG_EU = @'
{"schemaVersion":1,"name":"datadog-eu-prod","package":"datadog","packageVersion":"1.*","endpoints":{"default":"https://api.datadoghq.eu"},"auth":{"type":"apiKey","headers":{"DD-API-KEY":"kv://she-kv/dd-eu-api-key","DD-APPLICATION-KEY":"kv://she-kv/dd-eu-app-key"}},"batch":{"maxSize":50},"rateLimit":{"requests":300,"per":"1m"},"concurrency":4,"timeout":"30s","defaults":{"interval":"2m"},"settings":{}}
'@
$env:SHE_PLUGIN_DATADOG_US = @'
{"schemaVersion":1,"name":"datadog-us-prod","package":"datadog","packageVersion":"1.*","endpoints":{"default":"https://api.datadoghq.com"},"auth":{"type":"apiKey","headers":{"DD-API-KEY":"kv://she-kv/dd-us-api-key","DD-APPLICATION-KEY":"kv://she-kv/dd-us-app-key"}},"batch":{"maxSize":50},"rateLimit":{"requests":300,"per":"1m"},"concurrency":4,"timeout":"30s","defaults":{"interval":"2m"},"settings":{}}
'@
```

Signals select `plugin:"datadog-eu-prod"` or `plugin:"datadog-us-prod"`.
For Splunk use `package:"splunk-observability"`,
`endpoints:{"api":"https://api.eu0.signalfx.com","stream":"https://stream.eu0.signalfx.com"}`
and `auth:{"type":"apiKey","headers":{"X-SF-Token":"kv://she-kv/splunk-token"}}`.
All entries are validated before scheduling. Configuration changes require
redeployment. Secrets remain in Key Vault, never these JSON objects.

## Public vendor sources verified before implementation

Datadog/Splunk sources verified **2026-10-16**; Azure Monitor/Prometheus sources
verified **2026-10-21** against vendor documentation, not inferred API shapes:

- [Azure Monitor managed Prometheus query API](https://learn.microsoft.com/en-us/azure/azure-monitor/metrics/prometheus-api-promql):
  workspace query endpoint, instant-query POST, Entra token audience and
  Monitoring Data Reader role.
- [Prometheus HTTP API](https://prometheus.io/docs/prometheus/latest/querying/api/):
  form-encoded instant queries, evaluation timestamps, scalar/vector response
  shapes, numeric string samples, warnings and info annotations.

- [Datadog query timeseries points (v1)](https://docs.datadoghq.com/api/latest/metrics/query-timeseries-points/):
  site origins, GET query arguments, seconds vs milliseconds, pointlist and
  status/error shapes. [Markdown source](https://docs.datadoghq.com/api/latest/metrics/query-timeseries-points.md).
- [Datadog monitor details](https://docs.datadoghq.com/api/latest/monitors/get-a-monitors-details/):
  GET monitor ID, `overall_state` enum and configuration timestamps.
  [Markdown source](https://docs.datadoghq.com/api/latest/monitors/get-a-monitors-details.md).
- [Datadog authentication](https://docs.datadoghq.com/api/latest/authentication/)
  and [API/application keys](https://docs.datadoghq.com/account_management/api-app-keys/):
  read-key requirements and site separation.
- [Splunk SignalFlow API](https://dev.splunk.com/observability/reference/api/signalflow/latest):
  `POST /execute` under `/v2/signalflow`, stream origin, `start`/`stop`/
  `resolution` query arguments, `text/plain` or JSON `programText` payload,
  and `X-SF-Token`.
- [Splunk stream message specification](https://dev.splunk.com/observability/docs/signalflow/messages/stream_messages_specification):
  exact SSE `data` payload (`logicalTimestampMs`, `data[].tsId/value`),
  multiline examples, error/control messages and normal/aborted termination.
  **Use the URL without a trailing slash**; obsolete slash-suffixed links
  can redirect to the documentation home page.
- [Splunk detectors API](https://dev.splunk.com/observability/reference/api/detectors/latest):
  documented incidents operation, bare-array schema, pagination, `active`,
  `anomalyState` enum, `events[].timestamp`, and optional filtering done
  client-side. No `/status` operation was found in the current reference.
- Vendor-maintained [SignalFlow SSE client](https://github.com/signalfx/signalflow-client-python/blob/master/src/signalfx/signalflow/sse.py)
  and [message decoder](https://github.com/signalfx/signalflow-client-python/blob/master/src/signalfx/signalflow/messages.py)
  corroborate the documented text-body request and stream message shapes.

Splunk's current docs are client-rendered: their initial HTML text can look
empty, but the official embedded parsed API specification and MDX content
contain the schemas/examples above. Search-engine summaries are not an API
contract; verify the official content itself before changing an adapter.
