# Environment configuration and image packages

The Host composes configuration parsing, validation, package loading,
authentication, scheduling, polling and publishing. Offline `validate` loads the
same parser/catalog without contacting a sink, Azure, Key Vault or a health model.
See [host commands](host-runtime.md) and the authoritative
[package → image → configuration → deployment workflow](image-delivery.md).

## Startup contract

The only production configuration source is the process environment, copied once.
The parser also accepts a dictionary for offline validation and composition:

```csharp
ConfigSnapshot snapshot = new EnvironmentConfigSource().Load(environment);
using PackageCatalog catalog = PackageCatalog.Load(snapshot.Host.PackagesDirectory);
ConfigurationValidator.Validate(snapshot, catalog, ConfigurationValidationMode.Deployment);
var manager = new PluginManager(catalog, contextFactory);
// Only after the entire snapshot passes validation:
ISinkClient client = await manager.CreateClientAsync(snapshot.Plugins[0], cancellationToken);
```

Namespaces are `SelfHostedEvaluators.Core.Configuration` and
`SelfHostedEvaluators.Core.Packages`. Parsing validates the entire local snapshot:
schema, host values, names, auth data, uniqueness, effective intervals, TTLs and
budgets. Package-aware validation checks packages, versions, auth registration,
capabilities, settings and known queries. Nothing is partially activated on error.

`ConfigurationException.Diagnostics` contains only `(Variable, PropertyPath)`.
Messages contain those same locations, never JSON values, parser exception
messages or plugin exception/validation text. Package errors identify the manifest
path and property, or `SHE_PACKAGES_DIR:$` for directory discovery failures.
`ConfigSnapshot.Warnings` identifies entities exceeding 50 signals without rejecting
them. Collections are copied and frozen; retained settings, queries and custom
auth JSON are cloned, independent of input dictionaries and disposed documents.

## Variables

All names, prefixes and suffixes are case-sensitive. `<KEY>` matches
`^[A-Z][A-Z0-9_]*$`. Suffixes identify entries, not their JSON `name`.
Unknown reserved `SHE_` keys, empty supplied values and invalid optional settings
are errors; unrelated process environment variables are ignored.

| Variable | Requirement / default | Format |
| --- | --- | --- |
| `SHE_PLUGIN_<KEY>` | At least one | One JSON plugin object |
| `SHE_MODEL_<KEY>` | At least one | One JSON model object |
| `SHE_PACKAGES_DIR` | `/app/packages` | Local/image directory, never a URL |
| `SHE_DEFAULT_INTERVAL` | `1m` | Effective intervals: `10s` through `1d`, inclusive |
| `SHE_DEFAULT_TIMEOUT` | `30s` | Positive duration; execution uses `min(timeout, interval)` |
| `SHE_DEFAULT_TTL_MULTIPLIER` | `3` | Positive finite invariant decimal; effective TTL rules below |
| `SHE_REPORTS_PER_MINUTE` | `5000` | Integer `1..5999`, per model |
| `SHE_SECRET_CACHE_TTL` | `5m` | Positive duration |
| `SHE_SHUTDOWN_TIMEOUT` | `5s` | Positive best-effort combined drain/flush budget |
| `SHE_LOG_LEVEL` | `Information` | Exactly `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, `None` |
| `AZURE_CLIENT_ID` | Required by ACI deployment and whenever an Application Insights connection string is supplied | UAMI client UUID; default managed-identity selector and explicit telemetry credential |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | Required by ACI deployment; optional locally | Bicep-created Application Insights connection string; absent locally means console telemetry |
| `AZURE_FEDERATED_TOKEN_FILE` | Required for workload identity unless profile supplies `tokenFile` | Externally supplied token-file path; no file is read during validation |

Durations are a positive integer immediately followed by lower-case `s`, `m`,
`h` or `d`, within `TimeSpan` range. Numeric settings use invariant culture, not
group separators or locale decimals. No trimming, unit conversion guesswork,
quoted JSON strings, YAML, base64, file references, chunking or fallback source
is supported. JSON comments, trailing commas, duplicate properties (at any depth),
unknown core properties, wrong types and explicit `null` optional values fail.
JSON nesting is limited to 64 levels. Newlines in JSON formatting are allowed.

The parser is host-independent: it does not infer “ACI mode” or require ACI-only
variables during local/deployment validation. Infrastructure must supply the two
ACI-required variables. Merely supplying them does not obtain credentials.

## Plugin object

For `SHE_PLUGIN_DATADOG_EU_PROD`:

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

Required: `schemaVersion` (integer `1`), `name`, `package`, nonempty `endpoints`
object, `auth` object. `package` is an alphanumeric/hyphen directory ID with an
alphanumeric first character. Endpoint names start with a letter and contain
letters, digits, `_` or `-`. Endpoint URIs are absolute HTTPS with a host and
without user info, query or fragment. The package may require specific endpoint
names and restrict hosts; host allowlists match exact DNS hosts, ignoring case,
not suffixes or wildcard subdomains.

HTTPS URI normalization, including IDN host validation, is checked during
parsing for plugin endpoints, model URIs, OAuth token endpoints and token scopes.
Malformed hosts fail with the variable/property-path diagnostic only, not a
deferred URI exception or URI payload. Valid internationalized hosts are accepted.

Optional: `packageVersion`, `batch`, `rateLimit`, `concurrency`, `timeout`,
`defaults`, `settings`. Omitted limits are deliberately conservative:
`batch.maxSize = 1`, `rateLimit.requests = 1`, `rateLimit.per = 1s`,
`concurrency = 1`, and the host timeout. If a limits object is supplied,
all its fields are required and positive. Batch size must not exceed the package
cap. Plugin defaults permit only `interval`; settings default to `{}` and their
schema belongs to the package's `ValidateSettings`.

### Azure Monitor workspace (managed Prometheus)

Include the `azure-monitor` package in the image, then set
`SHE_PLUGIN_AZURE_MONITOR` to:

```json
{
  "schemaVersion": 1,
  "name": "azure-monitor-prod",
  "package": "azure-monitor",
  "packageVersion": "1.*",
  "endpoints": {
    "default": "https://workspace-name.region.prometheus.monitor.azure.com"
  },
  "auth": {
    "type": "entraId",
    "azure": { "type": "managedIdentity" },
    "scope": "https://prometheus.monitor.azure.com/.default"
  },
  "batch": { "maxSize": 50 },
  "rateLimit": { "requests": 60, "per": "1m" },
  "concurrency": 1,
  "timeout": "30s",
  "settings": {}
}
```

Replace the endpoint with the **Query endpoint** from the existing Azure Monitor
workspace's Overview page. Supply only its root HTTPS origin, not an API path.
Grant **Monitoring Data Reader** on that workspace to the identity in
`auth.azure`. The identity defaults to `AZURE_CLIENT_ID`; supply `clientId` to
select another attached managed identity. The existing workload identity and
service-principal Azure profiles are also supported inside `entraId.azure`.
Never configure a workspace shared key or put credentials in the query.

Add a signal to an existing model's `signals` array:

```json
{
  "entity": "checkout-api",
  "name": "checkout-error-rate",
  "plugin": "azure-monitor-prod",
  "interval": "1m",
  "query": {
    "promql": "sum(rate(http_requests_total{job=\"checkout\",status=~\"5..\"}[5m]))"
  },
  "evaluation": {
    "degraded": { "operator": "GreaterThan", "threshold": 5 },
    "unhealthy": { "operator": "GreaterThan", "threshold": 10 },
    "noData": "Unknown"
  }
}
```

Each poll executes an **instant** PromQL query at its scheduled time. Use PromQL
range selectors and functions such as `rate(metric[5m])` or
`avg_over_time(metric[5m])` for lookback calculations; there are no plugin
`window`, `aggregation`, or range-query options. The expression must produce a
finite scalar or one float-valued vector series. Aggregate or filter multiple
series in PromQL; the plugin never chooses an arbitrary series. An empty vector
is NoData, while malformed/unsupported results, non-finite values, query
annotations indicating potentially incomplete data, and HTTP errors are
failures (evaluated as Unknown). PromQL syntax is checked by Azure, not parsed
locally.

Settings are exactly `{}`; queries contain only a nonempty `promql` string of
at most 4096 characters without control characters. This queries managed
Prometheus metrics, **not Log Analytics logs or KQL**. The workspace, its metric
ingestion, role assignment, and any required private-network connectivity must
already exist. See [package delivery](image-delivery.md#azure-monitor-package)
and the [query response contract](plugin-authoring.md#azure-monitor-settings-and-queries).

## Model object

For `SHE_MODEL_CHECKOUT`:

```json
{
  "schemaVersion": 1,
  "name": "checkout",
  "healthModel": {
    "dataPlaneUri": "https://model-id.regional-zone",
    "apiVersion": "2026-06-01-preview",
    "auth": { "type": "managedIdentity" },
    "tokenScope": "https://data.cloudhealth.azure.com/.default",
    "reportsPerMinute": 5000
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
        "stateMap": { "OK": "Healthy", "Warn": "Degraded", "Alert": "Unhealthy" },
        "noData": "Unknown"
      },
      "context": "Monitor {{state}}: {{detail}}"
    }
  ]
}
```

Required: `schemaVersion: 1`, `name`, `healthModel.dataPlaneUri`, nonempty `signals`.
Model URIs are absolute HTTPS without user info, query or fragment. URI host case,
default ports, dot segments and trailing slashes normalize for uniqueness.
Plugin names and model configuration names must each be unique (ordinal,
case-sensitive); model URIs must be unique. Names (including entity, signal and
referenced plugin names) match exactly:
`^[a-zA-Z0-9][a-zA-Z0-9-]{1,258}[a-zA-Z0-9]$` — 3 through 260 characters,
no control characters. Each `(model URI, entity, signal)` is unique.

Optional model fields: `defaults` and the remaining `healthModel` fields.
API version defaults to `2026-06-01-preview`; `2026-10-01-preview` is also accepted.
Auth defaults to `managedIdentity`, using `AZURE_CLIENT_ID` if supplied.
`tokenScope` defaults to `https://data.cloudhealth.azure.com/.default`; overrides
must be absolute HTTPS scopes without user info, query or fragment.
The report budget defaults to the host budget and can be overridden with an
integer `1..5999`.

Signals require `entity`, `name`, `plugin`, `query` object and `evaluation`.
Optional: `interval`, `ttlMultiplier`, `expiresInMinutes`, `context`.
Model defaults allow `interval`, `ttlMultiplier`, `expiresInMinutes`.

| Effective setting | First supplied level wins |
| --- | --- |
| Interval | Signal, plugin defaults, model defaults, host |
| Timeout | Plugin, host; bounded by the batch interval at execution |
| TTL policy | Signal, model defaults, host multiplier |

At each TTL level, explicit `expiresInMinutes` overrides a multiplier. A
signal multiplier overrides a model's explicit TTL. Computed TTL is
`clamp(ceil(effective interval minutes × multiplier), 1, 10080)`. Explicit TTLs
must be integers `1..10080`. Either result must cover at least **twice** the
effective interval. A positive multiplier need not be at least two: ceiling can
make smaller multipliers valid for short intervals.

The budget is the sum of `60 / effective interval seconds` for **all** signals,
including unresolved ones. Rates are summed as exact rational numbers: equality
is allowed (for example, seven signals at `7m` fit a budget of `1`), and any
excess fails without rounding tolerance.
More than 50 signals on one entity produces a snapshot warning, not an error.

Evaluation is either thresholds (`unhealthy` required, `degraded` optional)
or a nonempty `stateMap`, never both. Thresholds are finite numbers; operators
are exactly `LessThan`, `LessThanOrEqual`, `GreaterThan`, `GreaterThanOrEqual`,
`Equal`, `NotEqual`. State-map input keys are case-insensitive, so case-only
duplicates fail. Targets and optional `noData` are exactly `Healthy`, `Degraded`,
`Unhealthy`, `Unknown`; `noData` defaults to `Unknown`.
Context template variables are only `{{value}}`, `{{state}}`, `{{healthState}}`,
`{{detail}}`, `{{observedAt}}`. Report rendering will redact and truncate the
result to **4096 characters**; report bodies are buffered and limited to **32 KiB**.
Those are report limits, not arbitrary limits on query JSON or template source.

`Runtime` validation accepts signals referencing missing plugin configurations;
the runtime consumer must report those as `Unknown`, not silently skip them.
`Deployment` validation rejects such references. Unavailable configured packages likewise produce runtime `Unknown`, while
deployment mode rejects them. An invalid discovered manifest or incompatible
package is still a startup failure; there is no older-version fallback.
Every other basic validation applies in both modes. Known queries are validated
by their actual package.

## Typed authentication data

All credential fields below are **references**, never passwords or tokens:
`kv://vault/name` or `kv://vault/name/version`. Vault/name/version use
alphanumeric/hyphen identifiers. User info, ports, query strings, fragments,
escaped segments and extra path segments are rejected.

| Sink `auth.type` | Fields besides `type` |
| --- | --- |
| `none` | None |
| `apiKey` | Nonempty `headers` map (header name to secret reference), **or** `queryParameter` plus `secret` reference |
| `bearer` | `token` reference |
| `basic` | `username`, `password` references |
| `oauth2ClientCredentials` | HTTPS `tokenEndpoint`, nonempty `clientId`, `clientSecret` reference, nonempty string array `scopes` |
| `mtls` | `certificate` reference |
| `entraId` | `azure` nested Azure profile and nonempty `scope` |

API-key headers are case-insensitive and must be valid HTTP token names;
query-parameter names contain letters, digits, `_`, `-`.

| Azure profile `type` | Fields besides `type` |
| --- | --- |
| `managedIdentity` | Optional UUID `clientId`; otherwise the host selector |
| `workloadIdentity` | UUID `tenantId`, UUID `clientId`, optional `tokenFile` (otherwise `AZURE_FEDERATED_TOKEN_FILE`) |
| `servicePrincipalSecret` | UUID `tenantId`, UUID `clientId`, `clientSecret` reference |
| `servicePrincipalCertificate` | UUID `tenantId`, UUID `clientId`, `certificate` reference |

Use Azure profiles for `healthModel.auth` or nested `entraId.azure`, not directly
as sink auth. Built-in type names and properties are case-sensitive.
Plugin-registered custom auth names are globally case-insensitive and cannot
collide with any built-in or another package. Custom profiles preserve cloned
extension JSON; their extension schema/credential interpretation belongs to the
registered scheme. Registration is checked without creating an auth context.

These records carry configuration only. The runtime auth registry supplies the injected
`Func<PluginConfiguration, CancellationToken, ValueTask<PluginInstanceContext>>`.
The host/factory owns its HTTP clients, auth contexts and rate limiters, including
cleanup after activation failure. Each successful call produces an independent
sink client owned by the caller; keep the catalog alive until clients finish.
Offline parsing and validation create no tokens, secret values or credentials.

## Image package contract

Packages are already-published image contents at `<packages dir>/<id>/<version>/`.
Each version contains `plugin.json`:

```json
{
  "id": "datadog",
  "version": "1.0.0",
  "assembly": "SelfHostedEvaluators.Plugins.Datadog.dll",
  "entryType": "SelfHostedEvaluators.Plugins.Datadog.DatadogPackage",
  "abstractionsVersion": "1.0.0"
}
```

Every manifest is strict, must agree with directory ID/version, and must declare
the host abstractions major (`1`). Versions use strict three-component SemVer
with optional prerelease/build identifiers. Selection uses SemVer precedence,
including numeric prerelease identifiers and ordinal ASCII label ordering.
Duplicate version identities (even with different build metadata) fail.
Only the highest bundled version per ID is loaded; a failed highest version or
constraint mismatch never falls back to an older version.

`packageVersion` supports exact SemVer, stable numeric floats (`*`, `1.*`,
`1.2.*`) and explicit NuGet-style bounded ranges such as `[1.0.0,2.0.0)` or
`[1.0.0,)`. A bare version is **exact**, not a minimum. Bounds must be full
SemVer. Version numbers must fit the .NET/NuGet version representation.

Manifests outside the canonical layout or nested duplicate manifests fail.
Assemblies must be relative DLL paths wholly within the selected package;
rooted paths, traversal, missing files, symlinks/junctions and resolver paths
outside that package are rejected. No runtime download, extraction or directory
outside the selected package is used for private dependencies.

Each selected package gets a collectible isolated `AssemblyLoadContext` using
`AssemblyDependencyResolver`. Only framework assemblies, the public plugin
abstractions, `Microsoft.Extensions.*.Abstractions`, and
**`System.Threading.RateLimiting`** share host identity. Other managed/native
dependencies resolve privately or fail explicitly; they never silently bind a
host copy. A public parameterless package entry and sink constructor are needed.
Registration must provide exactly one sink. Custom auth registrations are
recorded as types for subsequent authenticated construction.

## Size, deployment and rollback

Configuration must fit the actual target's ACI/ARM and process-environment size
limits. Scripts apply a conservative **32768-byte safety budget** to the entire
UTF-8 `name=value\0` environment block plus final terminator, including generated
values. This is not a universal platform limit or a guarantee of ACI acceptance.
Deployment permits lowering the ceiling, not bypassing it. Serialized parameters
plus template are capped at **3 MiB**; deployment also bounds resource counts,
tags, names, subnets and compute inputs. Both size guards fail explicitly without
chunking, storage, a remote last-good snapshot or watcher. Validate the complete
environment against the exact image before updating ACI.

Changing a shell environment does not change the running immutable snapshot.
Redeploy the **same** ACI group through Bicep to change configuration. It restarts
in place (not rolling revisions), creates a reporting gap and discards in-memory
work. Package changes require a rebuilt image and redeployment by immutable
digest. Roll back by redeploying the previous digest and its **complete**
environment. Keep non-secret parameter objects in version control. Removed
signals stop future reports and existing reports expire by TTL.

Key Vault secret rotation is independent of configuration changes: the active
secret resolver refreshes unversioned references after the cache TTL. A versioned
reference stays pinned; change that reference and redeploy to rotate it.
Shutdown flushing is best effort; `SHE_SHUTDOWN_TIMEOUT` does not extend ACI's
termination window or promise a configurable termination-grace period.

## Source-to-environment mapping

| Desired-state source | Environment in the worker |
| --- | --- |
| `pluginConfigurations: [{key:"DATADOG_EU", config:{...}}]` in Bicep/deploy parameters | `SHE_PLUGIN_DATADOG_EU` = compact JSON of `config` |
| `modelConfigurations: [{key:"CHECKOUT", config:{...}}]` | `SHE_MODEL_CHECKOUT` = compact JSON of `config` |
| `hostSettings: {SHE_DEFAULT_INTERVAL:"1m", ...}` | One scalar string per allowed host-setting key |
| Bicep UAMI output | `AZURE_CLIENT_ID` = identity **client UUID**, not resource ID |
| Bicep Application Insights component | `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| Image layout / deployment projection | `SHE_PACKAGES_DIR=/app/packages` |

Local `validate-config.ps1` uses the equivalent `-Plugins`, `-Models`,
`-HostSettings` arguments or a JSON `-ConfigurationFile` with `plugins`, `models`,
`hostSettings`. These arrays contain objects, **not pre-serialized config
strings**. `key` controls the environment suffix, `config.name` the runtime
identity, and `config.package` the bundled package ID; these are different fields.
For example, `DATADOG_EU` and `DATADOG_US` can have different names and credentials
while both select `package:"datadog"` from one ALC.

See [image delivery's Static example](image-delivery.md#validate-the-exact-imageconfiguration-offline)
and [plugin authoring's multi-sink example](plugin-authoring.md#multiple-named-sinks).
Secrets are never interpolated into either parameter format. Generated keys are
reserved: user `hostSettings` cannot override identity, telemetry or package path.
`AZURE_FEDERATED_TOKEN_FILE` is for externally configured local workload identity,
not a new ACI configuration transport.
