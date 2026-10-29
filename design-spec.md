# Self-Hosted Evaluator Design Spec
## 1. Summary

The Self-Hosted Evaluator is a single .NET 10 container that runs in Azure Container Instances (ACI). It does four things:

1. Polls external observability sinks: Datadog, Splunk Observability Cloud, or any other sink that has a plugin.
2. Evaluates the results into health states.
3. Builds health reports from those states.
4. Sends the reports to the Azure Monitor Health Models data plane. Each report updates one signal on an entity of an existing health model.

### 1.1 Requirements

| # | Requirement | How the design meets it |
| --- | --- | --- |
| R1 | Poll any sink with only a sink-specific plugin | Plugin packages implement a small `Plugins.Abstractions` contract. The core handles scheduling, auth, limits, evaluation and publishing. Each sink is a named plugin config that references a package (§4). |
| R2 | All infrastructure as IaC in Bicep | `infra/main.bicep` deploys at subscription scope, with modules and scoped built-in role assignments. The PowerShell scripts only build images, push them to ACR and invoke Bicep (§9). |
| R3 | Configurable polling and reporting intervals | Each signal has an interval, inherited from signal, then plugin, then model, then host. A report is sent after every poll, and its TTL is derived from the interval (§6). |
| R4 | Configurable authentication | Named auth schemes on the Azure side and the sink side, secrets from Key Vault, and plugin-registered schemes (§5). |
| R5 | No storage-account dependency | Configuration comes only from environment variables; plugin code is bundled in the image. No storage account, Blob client, Azure Files mount or storage RBAC is required. |

### 1.2 Not in scope for v1

- Creating health models or entities, or setting up the monitored sinks. A separate demo system creates models and entities. Signals don't need to be defined up front, because the first report for a signal creates it.
- CI/CD pipelines. Local PowerShell scripts only.
- More than one active replica, or high availability.
- A sandbox for plugins inside the process.
- The ARM `ingestHealthReport` action. The system uses the data plane only.
- Runtime configuration hot reload or plugin downloads. Changes require redeployment.

## 2. Key decisions

| Area | Decision |
| --- | --- |
| Language and runtime | .NET 10, C# |
| Hosting | ACI: one Linux container group with one evaluator container, Standard SKU, `restartPolicy: Always`, no public inbound endpoint |
| Identity | One user-assigned managed identity (UAMI) |
| Ingestion | Data plane: `POST {dataPlaneUri}/api/entities/{entity}/healthReports` |
| Scope | Reports for any existing health model, driven entirely by config |
| Scale | Small to medium: a few thousand signals across several models, subject to validated environment-size limits (§6.1) |
| Plugin code | Runs in the process, one `AssemblyLoadContext` per package. Published assemblies and manifests are bundled under `/app/packages` in the image. |
| One plugin per sink | A plugin is a named JSON environment entry for exactly one sink: endpoints, auth, batch size, rate limit, concurrency and timeout. It references a shared code package. |
| Scheduling | Signals are grouped by (plugin, interval), with aligned ticks and batched queries |
| Evaluation | Done in the core, using thresholds or maps from the sink's own states. Plugins only return values and states. |
| Reporting | One report after every poll |
| Poll failure | Report `Unknown` immediately, with the error in `additionalContext` |
| Config | One JSON environment variable per plugin and per model, validated as one immutable startup snapshot |
| Secrets | Key Vault references in the form `kv://{vault}/{name}[/{version}]` |
| Networking | Public by default. Bicep switches add a VNet with NAT Gateway static egress, and private endpoints. |
| Package integrity | Trusted image build and ACR RBAC; deploy an immutable image digest. Plugins are not independently signed (an accepted risk). |
| v1 packages | Datadog, Splunk Observability Cloud, and Static (a deterministic fake) |
| Build and deploy | Bicep plus PowerShell 7 scripts |

## 3. Architecture

### 3.1 Solution layout

| Path | Contents |
| --- | --- |
| `src/Plugins.Abstractions` | The public plugin contract, versioned with SemVer. Besides `Microsoft.Extensions.*.Abstractions`, it is the only assembly plugin authors reference. |
| `src/Core` | Config store, plugin manager, scheduler, poll executor, evaluator, report builder, publisher, auth and secrets |
| `src/Host` | Generic Host with DI and OpenTelemetry. Serves probes on loopback port 8080; `probe live` and `probe ready` commands query them for ACI exec probes. Provides `validate` and local execution using the same environment contract. |
| `src/Plugins.Datadog` | Datadog package |
| `src/Plugins.SplunkObservability` | Splunk Observability Cloud package |
| `src/Plugins.Static` | Deterministic fake package for tests and demos |
| `src/Plugins.Testing` | Shared test helpers for plugin authors |
| `tests/` | Unit and integration tests (NUnit) |
| `infra/` | Bicep templates and parameter files |
| `scripts/` | PowerShell 7 scripts |

The repo uses UTF-8 without a BOM and LF line endings, enforced by `.gitattributes` and `.editorconfig`.

### 3.2 Runtime components

The [companion system diagram](system-diagram.md) shows deployment and runtime connections. Configuration comes from the process environment; plugin assemblies come from the image; credential references resolve through Key Vault. The runtime pipeline is Scheduler -> Poll Executor -> Evaluator -> Report Builder -> Publisher, with sink clients invoked by the Poll Executor.

1. **Config Store**
   - Reads `SHE_PLUGIN_*` and `SHE_MODEL_*` JSON values, plus host settings, from the environment.
   - Builds one immutable snapshot at startup; it does not watch files or remote configuration.
   - Invalid configuration logs a redacted error naming the variable and JSON path, fails readiness and exits nonzero. No partial snapshot is activated.
2. **Plugin Manager**
   - At startup, discovers package directories already present under `/app/packages` in the image.
   - Loads the highest version of each package into its own `AssemblyLoadContext`.
   - Builds one `ISinkClient` per plugin config.
   - Environment changes require an ACI redeployment; package changes require a rebuilt image and redeployment.
   - No runtime package download, extraction, storage SDK or persistent volume is required.
3. **Secret Resolver and Auth Registry**
   - Resolves `kv://` references and caches them with a TTL.
   - Builds credential handlers from auth profiles.
4. **Scheduler:** groups signals by (plugin, interval) and fires aligned ticks.
5. **Poll Executor**
   - Splits each group into batches.
   - Applies the plugin's rate limit, concurrency and timeout.
   - Turns exceptions into failures for the affected signals.
6. **Evaluator:** applies threshold rules or state maps to produce a health state.
7. **Report Builder:** builds the report body and enforces name patterns and field limits.
8. **Publisher:** sends reports for each model, with rate limiting, retries and superseding.

## 4. Plugin model

### 4.1 Terms

- **Package:** a published code directory, for example `/app/packages/datadog/1.0.0`, included in the container image. It provides exactly one plugin type, named by its `id`, and can also register auth schemes.
- **Plugin:** a named JSON configuration, for example the value of `SHE_PLUGIN_DATADOG_EU_PROD`, that maps to exactly one external sink and references a package by `id`. Two Datadog sinks mean two environment entries. Both use the same package, with separate endpoints, auth, batch size and rate limit.

### 4.2 Package format

A package directory contains the `dotnet publish` output and a `plugin.json`:

```json
{
  "id": "datadog",
  "version": "1.0.0",
  "assembly": "SelfHostedEvaluators.Plugins.Datadog.dll",
  "entryType": "SelfHostedEvaluators.Plugins.Datadog.DatadogPackage",
  "abstractionsVersion": "1.0.0"
}
```

- The host loads a package only if its `abstractionsVersion` has the same major version as the host's own `Plugins.Abstractions`.
- `Plugins.Abstractions`, `Microsoft.Extensions.*.Abstractions` and the framework are shared with the host. Everything else loads privately into the package's `AssemblyLoadContext`.
- Directories follow `/app/packages/<id>/<version>/`. If several versions are bundled, only the highest is loaded. A plugin entry can set `packageVersion` (for example `"1.*"`). A mismatch fails configuration validation.
- The image build includes the selected first-party and third-party packages. Adding a package changes image contents, not the host's source or plugin contract. ACR delivers code; environment variables deliver configuration, never assemblies.

### 4.3 Contract

```csharp
public interface IPluginPackage                        // entry type named in plugin.json
{
    void Register(IPluginRegistrar registrar);        // SetSinkPlugin<T>(), AddAuthScheme<T>("splunkSession")
}

public interface ISinkPlugin                           // exactly one per package
{
    PluginCapabilities Capabilities { get; }           // MaxBatchSizeCap, RequiredEndpoints, AllowedHosts (optional)
    ValidationResult ValidateSettings(JsonElement settings);
    ValidationResult ValidateQuery(JsonElement query);
    ValueTask<ISinkClient> CreateClientAsync(PluginInstanceContext context, CancellationToken ct);
}

public interface ISinkClient : IAsyncDisposable        // one per plugin config
{
    Task<IReadOnlyList<SignalResult>> QueryBatchAsync(
        IReadOnlyList<SignalQuery> batch, QueryWindow window, CancellationToken ct);
}

// SignalResult = Observation(value?, nativeState?, observedAt, detail?)
//              | NoData(reason)
//              | Failure(code, message, retryAfter?)
```

- Results are matched to queries by signal key. If a query has no result, that signal fails.
- `QueryWindow` carries the tick's scheduled time and the group's interval. A plugin works out its lookback from these. It uses a per-query setting such as `query.window` if one is set, and the interval otherwise.
- `SingleQueryClientBase` lets authors implement `QueryAsync` for a single query. The base class fans a batch out with limited parallelism.

### 4.4 PluginInstanceContext

The core passes this to `CreateClientAsync`. It contains:

- The plugin name, its validated settings, and the effective batch size.
- One `HttpClient` for each named endpoint, already configured with:
  - the base address
  - the auth handler
  - the plugin's rate limiter
  - retries on 429 and 5xx that honor `Retry-After`, within the batch timeout
  - telemetry
- `IAuthContext` and the plugin's `RateLimiter`, for transports that aren't HTTP, such as WebSockets.
- An `ILogger` and a `TimeProvider`.

Plugins never see raw credentials.

### 4.5 Limits enforced by the engine

These apply per plugin and are shared by every model that uses it:

- **Batch size:** batches are split to `batch.maxSize`, which can't exceed the package's `MaxBatchSizeCap`.
- **Rate limit:** a token bucket counts every outbound request.
- **Concurrency:** the maximum number of batches in flight at once.
- **Timeout:** applies to each batch call. A timeout fails every signal in the batch.
- **Backpressure:** a 429 from the sink with `Retry-After` pauses only that plugin.

### 4.6 Isolation

An exception, timeout or hang in a batch becomes a `Failure` for each signal in that batch, and hung calls are counted. Isolation stops at the process boundary: there is no sandbox.

## 5. Authentication

### 5.1 Auth profiles

Auth is declared inline as a typed profile: in each plugin environment entry (`auth`) and each model entry (`healthModel.auth`).

| Side | Scheme | Notes |
| --- | --- | --- |
| Azure | `managedIdentity` | System-assigned, or user-assigned selected by `clientId` |
| Azure | `workloadIdentity` | Uses an externally supplied federated token file on compatible hosts; ACI deployment uses UAMI and does not provision federation or a token-file mount. |
| Azure | `servicePrincipalSecret` | Tenant, client ID, and a secret reference |
| Azure | `servicePrincipalCertificate` | Tenant, client ID, and a Key Vault certificate |
| Sink | `apiKey` | One or more headers, or a query parameter. Datadog uses `DD-API-KEY` and `DD-APPLICATION-KEY`; Splunk uses `X-SF-Token`. |
| Sink | `bearer` | A reference to a static token |
| Sink | `basic` | References to a username and password |
| Sink | `oauth2ClientCredentials` | Token endpoint, client ID and secret, and scopes. Tokens are cached until they expire. |
| Sink | `mtls` | A client certificate from Key Vault |
| Sink | `entraId` | An Azure scheme plus a scope |
| Sink | `none` | No auth |
| Any | Registered by a plugin | `AddAuthScheme<T>(name)`, for flows specific to one sink |

### 5.2 Secrets

- Secrets are written as references: `kv://{vault}/{name}[/{version}]`.
- Resolved values are cached with a TTL, so a rotated secret is picked up without a restart.
- Secret values are never logged.

### 5.3 Identities

- **Bootstrap identity:** the UAMI reads Key Vault and authenticates telemetry. Bicep passes its client ID in `AZURE_CLIENT_ID`. ACI separately uses the identity's resource ID to pull the image from ACR.
- **Data-plane identity:** set per model in `healthModel.auth`. The default is `managedIdentity`, which means the UAMI.
  - The token scope defaults to `https://data.cloudhealth.azure.com/.default`.
  - The scope can be overridden per model for non-production environments.
- Credentials are sent only over HTTPS, and only to the configured hosts. Clients that carry credentials don't follow redirects to a different host.

## 6. Configuration

### 6.1 Environment-variable contract

Environment variables are the only runtime configuration source in Azure and locally. No configuration URL, file mount, storage account or remote fallback is used.

| Variable | Required | Value and purpose |
| --- | --- | --- |
| `SHE_PLUGIN_<KEY>` | At least one | JSON object for one sink, with `schemaVersion: 1`, `name`, `package`, `endpoints` and `auth`; optional limits, defaults and package-specific `settings` (§6.2). |
| `SHE_MODEL_<KEY>` | At least one | JSON object for one model, with `schemaVersion: 1`, `name`, `healthModel.dataPlaneUri` and a nonempty `signals` array; optional auth and defaults (§6.3). |
| `AZURE_CLIENT_ID` | Required on ACI | Client ID of the attached UAMI. Bicep supplies it; this is not a secret. Used for default managed-identity auth, Key Vault and telemetry. |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Required on ACI | Connection string of the Bicep-created Application Insights resource. The exporter also uses the UAMI; the string alone is not an authentication credential. Optional locally; omission uses console telemetry only. |
| `SHE_PACKAGES_DIR` | No | Package directory; default `/app/packages`. ACI uses the image-bundled directory. Local tests may point to locally published packages. It is not a download URL. |
| `SHE_DEFAULT_INTERVAL` | No | Default polling/reporting interval; `1m`. Effective intervals must be from `10s` to `1d`. |
| `SHE_DEFAULT_TIMEOUT` | No | Default batch timeout; `30s`, positive and bounded by the effective interval at execution. |
| `SHE_DEFAULT_TTL_MULTIPLIER` | No | Default TTL multiplier; `3`, subject to §6.6. |
| `SHE_REPORTS_PER_MINUTE` | No | Default per-model report budget; `5000`, a positive integer strictly below `6000`. A model can override it with `healthModel.reportsPerMinute` within the same bounds. |
| `SHE_SECRET_CACHE_TTL` | No | Key Vault secret-cache lifetime; `5m`, positive. Unversioned references pick up rotations after cache expiry. |
| `SHE_SHUTDOWN_TIMEOUT` | No | Best-effort combined drain/flush budget; `5s`, positive. This does not extend ACI's termination window (§7.6). |
| `SHE_LOG_LEVEL` | No | Minimum log level; `Information`. Allowed values: `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None`. Redaction applies at every level. |
| `AZURE_FEDERATED_TOKEN_FILE` | Only for `workloadIdentity` | Path to an externally supplied token file on a compatible host. ACI's Bicep deployment neither sets this variable nor requires a volume for it. |

`<KEY>` must match `^[A-Z][A-Z0-9_]*$`. For example, `SHE_PLUGIN_DATADOG_EU_PROD` and `SHE_MODEL_CHECKOUT`. Keys identify environment entries; their JSON `name` values identify plugins and model configurations, and are validated independently. Duplicate names or model URIs are rejected. Prefixes and keys are case-sensitive; misspelled reserved `SHE_` settings are errors, not silently ignored.

Plugin/model values are JSON objects, not quoted JSON strings, YAML, base64 or file paths. Newlines are allowed but unnecessary. Host settings are plain strings; durations use an integer with `s`, `m`, `h` or `d`, and numeric settings use invariant culture. Invalid optional values fail startup instead of falling back to defaults.

Bicep accepts arrays of plugin/model objects, serializes each with `string(...)`, and supplies the resulting ACI `environmentVariables`. Operators may keep non-secret objects in local Bicep parameter files; the host still reads only the environment. All credential fields hold `kv://` references, never passwords or tokens. Configuration must fit ACI/ARM and process-environment limits; deployment fails explicitly when it does not. No automatic storage fallback or variable chunking is introduced.

`Host validate` reads exactly this environment contract and the selected package directory, validates without polling or publishing, and returns nonzero with redacted variable/path diagnostics on failure. It does not need to resolve secrets or obtain tokens. No `--config-dir` option is required.

### 6.2 Plugin environment entry

The value of `SHE_PLUGIN_DATADOG_EU_PROD`:

```json
{
  "schemaVersion": 1,
  "name": "datadog-eu-prod",
  "package": "datadog",
  "packageVersion": "1.*",
  "endpoints": { "default": "https://api.datadoghq.eu" },
  "auth": {
    "type": "apiKey",
    "headers": {
      "DD-API-KEY": "kv://she-kv/dd-eu-api-key",
      "DD-APPLICATION-KEY": "kv://she-kv/dd-eu-app-key"
    }
  },
  "batch": { "maxSize": 50 },
  "rateLimit": { "requests": 300, "per": "1m" },
  "concurrency": 4,
  "timeout": "30s",
  "defaults": { "interval": "2m" },
  "settings": {}
}
```

A Splunk plugin uses named endpoints, such as `api: https://api.eu0.signalfx.com` and `stream: https://stream.eu0.signalfx.com`, with `apiKey` auth sent in the `X-SF-Token` header.

### 6.3 Model environment entry

The value of `SHE_MODEL_CHECKOUT`:

```json
{
  "schemaVersion": 1,
  "name": "checkout",
  "healthModel": {
    "dataPlaneUri": "https://<model-id>.<regional-zone>",
    "apiVersion": "2026-06-01-preview",
    "auth": { "type": "managedIdentity" }
  },
  "defaults": { "interval": "1m", "ttlMultiplier": 3 },
  "signals": [
    {
      "entity": "checkout-api",
      "name": "p95-latency",
      "plugin": "datadog-eu-prod",
      "query": { "metric": "p95:trace.http.request{service:checkout}", "window": "5m" },
      "evaluation": {
        "degraded": { "operator": "GreaterThan", "threshold": 500 },
        "unhealthy": { "operator": "GreaterThan", "threshold": 1000 }
      }
    },
    {
      "entity": "checkout-api",
      "name": "checkout-monitor",
      "plugin": "datadog-eu-prod",
      "interval": "30s",
      "query": { "monitorId": 12345678 },
      "evaluation": {
        "stateMap": { "OK": "Healthy", "Warn": "Degraded", "Alert": "Unhealthy", "No Data": "Unknown" }
      },
      "context": "Monitor {{state}}: {{detail}}"
    }
  ]
}
```

### 6.4 Where settings come from

| Setting | Resolution order (first one set wins) |
| --- | --- |
| `interval` | signal, then plugin `defaults`, then model `defaults`, then host default |
| `timeout` | plugin, then host default. Only the plugin level applies, because one batch can include signals from several models. |
| `ttlMultiplier` / `expiresInMinutes` | signal, then model `defaults`, then host default |

In the examples above, `p95-latency` is polled every 2 minutes, taken from the plugin's `defaults`. `checkout-monitor` is polled every 30 seconds, set on the signal itself. The model's `interval: 1m` applies only to signals whose plugin doesn't set an interval.

### 6.5 Evaluation

- **Thresholds:** `unhealthy` is required and `degraded` is optional.
  - Operators: `LessThan`, `LessThanOrEqual`, `GreaterThan`, `GreaterThanOrEqual`, `Equal`, `NotEqual`.
  - The rules are also sent in the report as `evaluationRules`.
- **State map:** maps the sink's own state names to health states, ignoring case. A state that isn't in the map becomes `Unknown`.
- **No data:** `noData` sets the state to use when a query returns nothing. The default is `Unknown`.
- **Missing input:** a threshold signal whose observation has no value, or a state-map signal whose observation has no sink state, is treated as a poll failure (§7.3).
- **Context:** an optional template, rendered into `additionalContext`. The result is redacted and cut to 4,096 characters, as in §7.3. The template can use these variables:
  - `{{value}}`: the observed value.
  - `{{state}}`: the sink's own state.
  - `{{healthState}}`: the evaluated health state.
  - `{{detail}}`: detail text returned by the plugin.
  - `{{observedAt}}`: when the sink observed the value, in ISO 8601.

### 6.6 TTL

`expiresInMinutes = clamp(ceil(intervalInMinutes × ttlMultiplier), 1, 10080)`. Setting `expiresInMinutes` explicitly overrides the multiplier. Either way, the result must be at least twice the interval, so one late poll doesn't expire a signal.

### 6.7 Validation

Validation runs before scheduling. If any supplied environment entry or host setting is invalid, the complete startup snapshot is rejected and the host exits nonzero. Errors name the variable and JSON property path without printing the payload or secret values. Deployment runs `Host validate` before updating ACI.

**Plugin entries**

- The JSON object matches the schema and uses `schemaVersion: 1`.
- The name matches `^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$` and is unique.
- The referenced package is loaded. If `packageVersion` is set, the loaded version matches it.
- Endpoints:
  - All use HTTPS.
  - They include every name in the package's `RequiredEndpoints`.
  - If the package declares `AllowedHosts`, every endpoint host matches it.
- `batch.maxSize` is between 1 and the package cap.
- `rateLimit`, `concurrency` and `timeout` are positive.
- The auth scheme is known and its secret references are well formed.
- `ISinkPlugin.ValidateSettings` passes.

**Model entries**

- The JSON object matches the schema, uses `schemaVersion: 1`, and has a unique configuration name.
- `healthModel.dataPlaneUri` is HTTPS, has no user info, query string or fragment, and is unique after normalization. Explicit auth uses a known scheme.
- Entity and signal names match the pattern.
- Each (model, entity, signal) combination is unique across all entries.
- Each signal's query passes the plugin's `ValidateQuery`.
- Each effective interval is between 10 s and 1 d.
- The model stays within its report budget: 5,000 per minute by default, below the service's limit of 6,000 per minute.
- An entity with more than 50 signals produces a warning, not a rejection.
- A signal that references a missing or unavailable plugin is accepted. At runtime it reports `Unknown` with the reason. `Host validate` treats this case as an error.

### 6.8 Updating configuration and packages

- Environment variables are a startup snapshot. Changing shell or deployment parameters does not change an already running process.
- Redeploy the same ACI container group through Bicep to change plugin/model entries or host settings. ACI restarts the container in place; this is not a rolling revision update.
- Validate the full proposed configuration against the exact deployment image before redeploying. On failure, do not update the existing group.
- Adding, updating or removing a package requires building and pushing a new image and redeploying with its immutable digest.
- Removing a model or signal entry stops future reports after redeployment. Existing signals expire through their TTL.
- Roll back by redeploying the previous image digest and its complete environment configuration. Preserve non-secret parameter files in version control; there is no remote last-good snapshot.
- ACI updates cause a reporting gap and discard in-memory work. Key Vault secret rotation remains supported through the secret cache and does not require a configuration update.

## 7. Data flow and error handling

The pipeline is: Scheduler → Poll Executor → Evaluator → Report Builder → Publisher.

### 7.1 Scheduling

- Signals from all model entries are grouped by (plugin, interval).
- Ticks fall on multiples of the interval. Each group gets a fixed offset so that load is spread out.
- Every signal is polled once immediately at startup, including after a redeployment.
- A group never runs two ticks at once. If a tick overruns, the next tick is skipped and counted.

### 7.2 Polling

- Each tick is split into batches of the plugin's batch size. A batch can include signals from several models.
- Batches respect the plugin's concurrency and rate limit.
- The batch timeout is the plugin's timeout, but never longer than the interval.
- 429 and 5xx responses are retried within the batch timeout, honoring `Retry-After`.
- A signal fails if it times out, throws an exception, gets no result, or gets a value that isn't a finite number.

### 7.3 Evaluation

- A failure produces `Unknown` immediately, with `additionalContext` set to `Poll failed: {code}: {message}`.
- That text is redacted (secrets, auth headers and URL query strings are removed) and cut to 4,096 characters.
- Successful results are evaluated as described in §6.5.

### 7.4 Publishing

- **Request:** `POST {dataPlaneUri}/api/entities/{entity}/healthReports?api-version={apiVersion}`.
  - The JSON body is buffered and sent with `Content-Length`, never chunked.
  - `x-ms-client-request-id` stays the same across retries, and `x-ms-return-client-request-id: true` is sent so the service echoes it back.
- **Rate limit:** a token bucket per model, 5,000 per minute by default.
- **One report per signal:** at most one report per signal is in flight. A newer report replaces an older one that hasn't been accepted yet.
- **Retries:**
  - Retried on 408, 429, 5xx and network errors.
  - The wait comes from `Retry-After` (in seconds or as an HTTP date). Without that header, the wait is a jittered backoff of up to 30 s.
  - Retrying continues until the report is accepted, replaced by a newer one, or past its TTL.
  - A 429 pauses the whole model until the `Retry-After` time.
- **No retry:** 400, 401, 403, 404, 413 and 415 are logged and counted, not retried.

### 7.5 Startup checks

These checks only produce warnings and never block reporting. They run after the startup snapshot is validated and every 30 minutes after that. Configuration validation errors, unlike remote availability warnings, block startup.

- `GET /api` confirms the model exists and the credentials work.
- `GET /api/entities/{entity}` for each entity confirms the entity exists and compares its signal count with the 50-signal cap.

### 7.6 Shutdown

On SIGTERM:

1. Stop scheduling, and start failing the readiness probe.
2. Finish in-flight polls.
3. Flush pending reports.
4. Exit.

Steps 2 and 3 share `SHE_SHUTDOWN_TIMEOUT` (default `5s`). ACI's selected resource API exposes no configurable termination-grace property, so this is best effort, not a guaranteed drain window. Do not carry over a 60 s hosting guarantee. A forced stop may lose reports and final telemetry before cleanup runs. Reports still pending when the host's budget expires are dropped and counted when possible; signals keep their previous state until a new report arrives or their TTL expires. No persistent report queue is introduced.

## 8. Health Models data-plane contract (reference)

| Item | Value |
| --- | --- |
| Ingest route | `POST {dataPlaneUri}/api/entities/{entityName}/healthReports?api-version=…`, which returns `202` with no body |
| API versions | `2026-06-01-preview` (the default) and `2026-10-01-preview` (preview). Configurable per model. |
| Token scope | `https://data.cloudhealth.azure.com/.default` |
| RBAC | Ingest needs `Microsoft.CloudHealth/healthmodels/entities/ingestHealthReport/action`. This is a regular action, not a data action, and is inherited from model scope. The startup checks also need `healthmodels/read` and `healthmodels/entities/read`. Built-in Contributor covers these and is assigned only at each model. It also allows model/child modification and deletion; that broader model-scoped access is accepted. Monitoring Contributor lacks the ingest action. |
| Addressing | The service takes the model ID from the first DNS label of `dataPlaneUri`, so config must supply that URI. |
| Names | `signalName` and `entityName` must match `^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$` |
| Body | See the list below |
| Limits | See the list below |
| Client | There is no public data-plane SDK, so the calls are written by hand with `HttpClient`, using Azure.Identity tokens. |

**Body fields.** The entity is named in the route, not in the body.

- `signalName`: required.
- `healthState`: required. One of `Healthy`, `Degraded`, `Unhealthy` or `Unknown`.
- `value`: optional, must be finite.
- `evaluationRules`: optional. `unhealthyRule` is required, `degradedRule` is optional, and `Dynamic` is rejected.
- `expiresInMinutes`: 1 to 10080. The default is 60.
- `additionalContext`: at most 4,096 characters.

Example body for the `p95-latency` signal in §6.3. The TTL is 2 minutes × 3 = 6 minutes:

```json
{
  "signalName": "p95-latency",
  "healthState": "Degraded",
  "value": 742.5,
  "evaluationRules": {
    "degradedRule": { "operator": "GreaterThan", "threshold": 500 },
    "unhealthyRule": { "operator": "GreaterThan", "threshold": 1000 }
  },
  "expiresInMinutes": 6
}
```

**Limits**

- The body can be at most 32 KiB, otherwise the response is `413`.
- 6,000 calls per 60 s per model and subscription. Over that, the response is `429` with `Retry-After`.
- `503 ServiceBusy` is returned with `Retry-After: 10`.
- `Retry-After` can be a number of seconds or an HTTP date.
- Other errors: `400` for an invalid body, `401` or `403` for auth failures, `404` when the model isn't found, and `415` for the wrong content type.
- Every response includes `x-ms-request-id`.

**How reports are processed**

- A `202` means the report is queued. Reports for the same signal keep their order.
- Signals are created automatically, but entities must already exist.
- A report for a missing entity, or for an entity that already has 50 signals, is silently dropped.
- When a signal's TTL expires, the signal is removed.
- A report that expires before it is processed is dropped.
- If duplicates arrive, the last one wins.

## 9. Infrastructure and scripts

### 9.1 Bicep

`infra/main.bicep` deploys at subscription scope using `main.bicepparam` and creates one resource group containing these modules.

| Module | Resources and settings |
| --- | --- |
| `identity` | Reporter UAMI |
| `monitoring` | Log Analytics, and Application Insights with local auth disabled. The UAMI gets Monitoring Metrics Publisher. |
| `registry` | ACR with the admin user disabled. The UAMI gets AcrPull. Basic SKU, or Premium in `private` mode. |
| `keyvault` | RBAC mode with purge protection. The UAMI gets Key Vault Secrets User and Certificate User. Admins get Secrets Officer. |
| `networking` (optional) | VNet, ACI-delegated subnet, NAT Gateway and Standard public IP; separate private-endpoint subnet and DNS zones in `private` mode. |
| `containerGroup` | `Microsoft.ContainerInstance/containerGroups@2025-09-01`, as described below. No managed environment, revisions or scaling rules. |
| `health-model-access` | Assigns built-in Contributor (`b24988ac-6180-42a0-ab88-20f7382dd24c`) to the UAMI on one existing model, using the target model subscription's role definition. `main` loops over `healthModelResourceIds`. No custom role is created. |
| `alerts` (optional) | Created only when `actionGroupId` is set (§10.5) |

No storage module, storage role assignment, storage private endpoint, Azure Files volume, deployment script resource requiring storage, or config/package container output is defined.

**`containerGroup` module**

- One Linux container group, Standard SKU, containing only the evaluator. `restartPolicy: Always` restarts exited container processes; an explicit group stop still stops the workload. This is not application high availability.
- Configurable `cpu` and `memoryInGB` parameters, default `1` vCPU and `2` GiB. No autoscaling or replica setting.
- The container group attaches the UAMI. `imageRegistryCredentials` uses its resource ID and the ACR login server, not registry passwords. Deployment waits for AcrPull propagation before provisioning ACI.
- The image reference is pinned to `sha256`; no mutable `latest` tag is used for deployment.
- No public IP, DNS label, inbound application port or public probe endpoint. Probes bind to `127.0.0.1:8080`; ACI exec commands run the host's `probe ready` and `probe live` subcommands. No shell or curl is required in the chiseled image.
- ACI supports readiness and liveness probes, not the former startup-probe setting. Configure a liveness initial delay (default `60s`) and probe thresholds through Bicep. Readiness alone does not stop the outbound worker; the host gates scheduling on successful initialization.
- Set the environment variables in §6.1, including Bicep-generated `AZURE_CLIENT_ID` and `APPLICATIONINSIGHTS_CONNECTION_STRING`. Never configure a storage URL or storage key.
- No persistent volume. Package contents come from the image and report queues remain in memory.
- Application telemetry is exported directly using the UAMI. Platform deployment and lifecycle events are inspected through ACI's ARM/CLI interface; no workspace key is passed to the process.

**Configuration parameters**

- `pluginConfigurations` and `modelConfigurations` are arrays of `{ key, config }` objects. `key` is the environment suffix from §6.1; `config` is the JSON object in §6.2 or §6.3. Duplicate keys are rejected before deployment.
- `hostSettings` supplies optional `SHE_` settings from §6.1. Generated identity/telemetry values and reserved configuration prefixes cannot be overridden through this map.
- Keep all configuration values non-secret. Auth profiles contain Key Vault references; secrets are populated separately by admins and are never template outputs.
- `imageDigest`, compute/probe settings, `networkMode`, `healthModelResourceIds`, `adminPrincipalIds` and optional `actionGroupId` complete the deployment inputs.

**The `networkMode` parameter**

- `public`: the default. No VNet and no inbound public endpoint; outbound connections use public service endpoints with no fixed egress IP.
- `vnetNat`:
  - Adds a dedicated subnet of /24 or larger, delegated to `Microsoft.ContainerInstance/containerGroups`. It contains only ACI container groups.
  - Adds a NAT Gateway with a static Standard public IP. The IP is an output so sink owners can add it to their allow-lists.
  - Associates NAT with the ACI subnet; NAT is required for supported ACI VNet outbound connectivity.
- `private`:
  - Everything in `vnetNat`.
  - A separate, nondelegated subnet hosts private endpoints for Key Vault and Premium ACR, with their private DNS zones linked to the VNet. No storage private endpoint or DNS zone.
  - Public network access to Key Vault and ACR is disabled; ACR trusted-service access is enabled for managed-identity image pulls.
  - The operator's build/push and secret-management commands must run from a machine with private-network/DNS access. An operator IP allow-list cannot bypass disabled public network access.
  - Configure container-group DNS explicitly (Azure DNS `168.63.129.16` for this Azure VNet design); ACI does not inherit VNet DNS settings.
  - External sinks, Health Models and telemetry still need outbound HTTPS through NAT. This mode privatizes supporting services, not every external destination.

Use Azure public-cloud regions that support ACI VNet and UAMI together. Deployment preflight rejects unsupported cloud/region combinations; Azure Government VNet plus managed identity is not supported by this design.

**Outputs**

- The UAMI's principal ID and client ID.
- The built-in Contributor role definition ID in the deployment subscription.
- The egress IP, in `vnetNat` and `private` modes.
- The ACR login server.
- The Key Vault URI.
- The ACI container-group resource ID and name.
- The deployed image digest. Do not output environment payloads or secret values.

The person deploying needs Owner or User Access Administrator, because the template creates role assignments.

### 9.2 Scripts (PowerShell 7)

| Script | Purpose |
| --- | --- |
| `deploy.ps1` | Runs what-if, deploys prerequisites with `deployContainerGroup: false`, builds/pushes the image, validates configuration against that image, then invokes Bicep with its immutable digest and full environment configuration. Updates use the same container-group name and restart it. |
| `build-image.ps1` | Publishes the host and selected plugins, stages package directories and manifests, includes trusted third-party packages, builds/pushes the image and returns its digest. No package upload/download occurs at runtime. |
| `validate-config.ps1` | Maps parameter objects into the documented environment entries and runs `Host validate` inside the exact deployment image, without contacting sinks or the data plane. Failure stops deployment. |
| `test.ps1` | Runs all tests |
| `verify-e2e.ps1` | After an end-to-end deployment, reads entity snapshots back from the data plane |

All Azure resources and role assignments are defined by Bicep; scripts orchestrate operations, not imperative resource creation. There is no `publish-packages.ps1` or `upload-config.ps1`.

Image/environment changes redeploy the same group. CPU, memory, restart-policy, OS, zone or incompatible networking changes may require deletion and recreation; `deploy.ps1` must identify these changes and require explicit confirmation, not silently delete resources. Each update sends the complete desired state, because omitted ACI properties can revert to defaults.

## 10. Observability

Telemetry goes to Application Insights through the Azure Monitor OpenTelemetry distro, authenticated with the UAMI rather than a connection-string key.

### 10.1 Metrics

All metrics use the meter `SelfHostedEvaluators`. Tags are limited to `model`, `plugin` and `package`, never individual signals, so the number of time series stays bounded.

| Metric | What it measures |
| --- | --- |
| `evaluator.polls` and `evaluator.poll.duration` | Batches polled, by outcome, and how long they took |
| `evaluator.evaluations` | Evaluations, by resulting health state |
| `evaluator.publishes` | Publish attempts, by HTTP status |
| `evaluator.reports.superseded` and `evaluator.reports.dropped` | Reports replaced by a newer report, or dropped because they expired or were rejected |
| `evaluator.ratelimit.wait` | Time spent waiting on sink or data-plane rate limits |
| `evaluator.schedule.lag` and `evaluator.ticks.skipped` | How late ticks start, and ticks skipped because the previous one overran |
| `evaluator.config.loads` and `evaluator.package.loads` | Config and package loads, by outcome |
| `evaluator.checks.warnings` | Startup-check warnings (§7.5) |
| `evaluator.signals.active` | Number of active signals, as a gauge |
| `evaluator.host.starts` | Host process starts, as a counter; each start also emits a structured event for restart-loop alerts |

### 10.2 Traces

- Each group tick is a span.
- Each batch and each publish is a child span.
- Publish spans record `x-ms-request-id` and the client request ID, so a report can be traced to the service's logs.

### 10.3 Logs

- Logs are structured.
- A signal's state is logged at Information level only when it changes. Everything else is at Debug.
- Every config or package load writes one summary line: what was accepted, what was rejected, and why.
- Headers and secrets are redacted before anything is logged (§12).

### 10.4 Probes

These are served on `127.0.0.1:8080`, not exposed on an ACI public or private endpoint. ACI invokes `Host probe ready` and `Host probe live`, which make loopback HTTP requests and return zero on success or nonzero on failure. Probe commands never start the worker or load sink credentials.

- `/healthz/ready`, used by the readiness exec probe:
  - Passes after packages, the complete valid environment snapshot, and required sink clients have initialized.
  - Fails during shutdown.
- `/healthz/live`: fails if initialization or the scheduler heartbeat stalls beyond its allowed window, which causes ACI to restart the process under `Always`.
- ACI has no separate startup probe in the selected API. The liveness initial delay/thresholds allow initialization to finish. Readiness does not schedule or suppress polls; the host controls that explicitly.

### 10.5 Alerts (optional)

These are created only when `actionGroupId` is set.

- No `202` responses in the last 15 minutes. This covers an evaluator outage, which the model itself only shows later when signals expire (§12).
- More than 3 `evaluator.host.starts` events in 15 minutes. This is an application restart-loop signal, not an unsupported ACI restart-count platform metric; repeated deliberate redeployments can also trigger it.

## 11. Testing

All tests use NUnit and are run by `scripts/test.ps1`.

**Unit tests**

- Evaluator: threshold rules, state maps, inheritance of settings, TTL, validation.
- Publisher:
  - Both `Retry-After` formats, backoff, and superseding.
  - The per-model rate limiter.
  - Bodies are buffered, not chunked.
- The redactor.
- Scheduler, using `FakeTimeProvider`: aligned ticks, offsets, skipped ticks.
- Limits enforced by the engine:
  - Batch splitting against `MaxBatchSizeCap`.
  - The per-plugin rate limiter and concurrency.
  - A 429 pauses only that plugin.
- Environment parsing: multiple plugin/model entries, malformed JSON, missing required variables, duplicate keys/names/URIs, unknown reserved settings, schema versions, duration/budget bounds and redacted errors.
- Startup gating: no schedules or reports start from an invalid snapshot.
- Best-effort drain and cancellation within the configured shutdown budget.

**Plugin test helpers (`Plugins.Testing`)**

- A reusable NUnit base fixture that every package's tests inherit.
- It checks batching, cancellation, timeouts, one result per signal, and error mapping.

**Package tests**

- Datadog and Splunk are tested against WireMock.Net stubs of their APIs.

**Core and host tests**

There is no separate integration-test project. The `Core.Tests` and `Host.Tests` projects run the host runtime in-process against offline fakes (fake sinks, credentials and transports). They cover:

- 429 and 503 handling (`PublisherTests`, `PollExecutorTests`).
- Dependency conflicts between packages loaded in separate `AssemblyLoadContext`s, and rejection of a package built against an unsupported major version of the abstractions (`PluginManagerTests`, `PackageCatalogTests`).
- Loopback health endpoints and exec probe exit codes, without curl or a shell (`ProbeCommandTests`).
- Secret-cache refresh after Key Vault rotation (`SecretResolverTests`).
- Graceful shutdown on cancellation (`ShutdownTests`).

`scripts\test-container.ps1` covers image-bundled package discovery and selected-package inclusion against the built image. Environment snapshots (changes are picked up only by a new host process, not through hot reload) are a design property that automated tests do not exercise.

**Infrastructure and scripts**

- `scripts\test.ps1` runs `bicep build` and `bicep build-params` for `infra\main.bicep` and `main.bicepparam`, failing on compiler warnings.
- PSScriptAnalyzer on all scripts.
- There are no automated template assertions, network-mode checks or script behavior tests. The what-if run in `deploy.ps1` and the manual end-to-end test below cover the ACI resource type, `Always`, attached UAMI and identity-based image pull, environment entries, exec probes, no public ports, no storage-account resources or volumes, all three network modes, validate-first updates, and explicit confirmation before destructive group recreation.

**Manual end-to-end test**

1. Deploy with the Static package against a test health model.
2. Run `verify-e2e.ps1`, which reads the entity snapshots back and checks the expected states.
3. Redeploy changed environment values without rebuilding the image; verify the changed interval/signal behavior and removed signals' TTL expiry.
4. Redeploy an image with a new plugin package; verify discovery and digest-based rollback.
5. Confirm liveness-triggered restarts, no exposed probe ports, UAMI-based ACR pulls, and private-mode connectivity from an authorized network.

## 12. Security and accepted risks

### 12.1 Least privilege

The container runs as one UAMI. Built-in role assignments are limited to their required resources:

| Role | Scope |
| --- | --- |
| Key Vault Secrets User and Key Vault Certificate User | The vault |
| AcrPull | The registry |
| Monitoring Metrics Publisher | Application Insights |
| Contributor | Each listed health model, and nothing wider; includes model/child modification and deletion as an accepted risk |

### 12.2 Hardening

- Secrets live only in Key Vault. JSON environment entries and Bicep parameter objects hold `kv://` references, never secret values. Ordinary ACI environment values are visible to authorized resource readers and must not contain secrets.
- The redactor removes secrets, auth headers and URL query strings from logs, traces and `additionalContext`.
- Credentials are sent only over HTTPS, and only to hosts in the plugin entry.
- Redirects to a different host are never followed.
- When a package declares `AllowedHosts`, the plugin entry can only target those hosts.
- The image is a chiseled .NET image that runs as a non-root user.
- Published packages are immutable image contents; the host never replaces or extracts them at runtime.
- Every package load logs its id, version and SHA-256. Deployment records the image digest and parameter-source revision without logging configuration payloads.
- Version-controlled non-secret parameters and immutable image digests make deployments auditable and reversible. No storage account, storage credentials or storage roles are present.

### 12.3 Accepted risks

1. **Package integrity relies on trusted builds and registry/deployment access.** Packages are not independently signed. Anyone allowed to supply a deployment image can execute code as the UAMI. Restrict ACR push and ACI deployment rights; deploy immutable digests.
2. **Anyone who can change plugin environment configuration must be trusted just as much.** An entry can point at any host (unless the package sets `AllowedHosts`) and can use any secret the UAMI can read.
3. **Plugins are not sandboxed.** They run in-process with the host's permissions. `AssemblyLoadContext` separates dependencies, not privileges.
4. **An evaluator outage is not reported as `Unknown`.** If the evaluator stops, its signals keep their last state until their TTL expires and they are removed. The optional alerts in §10.5 cover this gap.
5. **`additionalContext` is readable** by anyone with read access to the health model. It is redacted, but sink error messages can still reveal some detail.
6. **ACI updates interrupt reporting.** The single group restarts in place, with no rolling revisions. Pending reports are memory-only, and forced termination may bypass the best-effort flush. The model's TTL and optional telemetry alerts cover, but do not eliminate, the gap.
7. **Environment configuration is bounded in size.** Per-entry variables reduce monolithic payloads but do not bypass ACI/ARM or process-environment limits. Validate realistic signal counts before deployment; oversized configurations fail explicitly rather than silently moving to storage.

## 13. References

- [Health report ingestion in Azure Monitor health models](https://learn.microsoft.com/en-us/azure/azure-monitor/health-models/health-report-ingestion)
- [ACI container-group Bicep reference](https://learn.microsoft.com/en-us/azure/templates/microsoft.containerinstance/2025-09-01/containergroups)
- [Updating an ACI container group](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-update)
- [ACI virtual-network scenarios and limitations](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-virtual-network-concepts)
- [ACI managed-identity image pulls from ACR](https://learn.microsoft.com/en-us/azure/container-instances/using-azure-container-registry-mi)
- [ACI liveness probes](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-liveness-probe)
- [ACI readiness probes](https://learn.microsoft.com/en-us/azure/container-instances/container-instances-readiness-probe)
