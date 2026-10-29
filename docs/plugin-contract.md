# Plugin contract, version 1.0.0

Reference `SelfHostedEvaluators.Plugins.Abstractions` (`net10.0`).
Its public namespace is `SelfHostedEvaluators.Plugins.Abstractions`;
it depends only on logging abstractions and `System.Threading.RateLimiting`,
not Core, Host, a concrete sink or an Azure SDK.

## Shared-assembly compatibility policy

The public contract keeps the explicit .NET `RateLimiter` type. The
package loader shares the host's `System.Threading.RateLimiting` assembly,
in addition to the abstractions, logging-abstractions and framework assemblies
listed in the reviewed spec; it must not load a package-private copy.
This is a narrow, required addition to that shared-assembly policy because
`RateLimiter` crosses the public boundary, not permission to share arbitrary
package dependencies. Other package dependencies remain private.
Package-loading tests prove this type-identity sharing with an actual loaded package.
The authoritative copied spec remains unchanged.

## Package registration and signals

- Implement `IPluginPackage.Register(IPluginRegistrar)`.
- Call `SetSinkPlugin<T>()` exactly once for a concrete `ISinkPlugin` type.
  `PluginRegistrar.Build()` rejects missing sinks; a second registration fails,
  even if it names the same type.
- Optional `AddAuthScheme<T>(name)` registers a concrete `IAuthScheme` factory.
  Names are case-insensitive. Duplicate names fail. The host must pass all
  built-in and previously registered package scheme names to the next
  `PluginRegistrar` constructor, making collisions across packages fail too.
  Registration records types, not activated instances; Core's package catalog
  loads registrations and `PluginManager` activates configured clients.
- `PluginCapabilities` declares the maximum batch size, required endpoint
  names and an optional host allowlist. `ValidationResult` carries success or
  nonempty, **redacted** errors.
- Each configured plugin gets a separate `ISinkClient`. All query/result
  matching uses the entire `SignalKey(ModelUri, Entity, Name)`, not `Name`.
- `QueryWindow(ScheduledAt, Interval)` is the scheduled tick, not the time the
  request happened. Lookback policy belongs to the sink.
- Return keyed `SignalResult.Observation`, `.NoData` or `.Failure`.
  Observations carry nullable numeric/native state values, a required
  observation time and optional detail. Failures carry a code, redacted
  message and optional `TimeSpan` retry delay.

## Contexts and credential boundary

`PluginInstanceContext` is constructed publicly by Core. It carries the plugin
name, cloned settings JSON, effective batch size, an immutable named
`HttpClient` mapping, `IAuthContext`, the shared plugin `RateLimiter`,
`ILogger` and `TimeProvider`. Settings contain references, never resolved
credentials. Query JSON is cloned too, so caller-owned `JsonDocument`s may
be disposed.

Core supplies HTTP clients already configured with base addresses, auth,
rate limiting, bounded retries and telemetry. Sink code uses these clients
directly and must not authenticate them again. For non-HTTP transports it
uses the auth context and acquires a lease from the shared rate limiter for
each outbound request.

`IAuthContext` offers operations, not credential/token getters:

1. `ConfigureHttpHandlerAsync(destination, HttpClientHandler, ct)` runs before
   the handler is used, allowing mTLS certificate configuration.
2. `AuthenticateAsync(HttpRequestMessage, ct)` applies per-request auth.
3. `AuthenticateWebSocketAsync(destination, ClientWebSocketOptions, ct)` runs
   before connecting, for WebSocket headers/certificates.
4. `Scheme` identifies the scheme; `DisposeAsync` releases auth-owned resources.

The construction side is separate: `IAuthScheme.CreateContextAsync` receives
an `AuthSchemeContext` containing cloned profile JSON, `ISecretResolver`,
a host-supplied HTTP client for auth exchanges, logging and time. Only this
auth-construction context exposes secret resolution. `ISecretResolver` resolves
reference URIs to secret strings (including encoded certificate payloads);
Core's `SecretResolver` implements Key Vault resolution and TTL caching.
The resulting client context never contains that resolver or raw values.

The host owns the supplied HTTP clients, auth contexts and rate limiters;
sinks must not dispose them. A sink owns and disposes resources it creates.
The auth-exchange HTTP client must be bounded/rate-limited too. Concrete auth
implementations must enforce approved HTTPS/WSS destinations and reject
cross-host redirects. Unsupported auth operations must fail explicitly, not
silently omit authentication. Core enforces these rules for supplied transports.
Plugins are trusted code:
auth operations mutate transport objects and are not an in-process sandbox.

## Single-query helper

Derive from `SingleQueryClientBase(maxConcurrency)` and implement `QueryAsync`.
The positive limit bounds fan-out **within one batch**; the engine separately
limits concurrent batches per plugin. Results retain input ordering and keys.
Missing/wrong-key results map to `invalid_result`, timeouts and independent
transport cancellations to `timeout`, and other exceptions to `query_failed`,
without copying exception messages that may contain credentials. Explicit
plugin failures retain retry metadata. Caller cancellation propagates as
`OperationCanceledException`, including for an empty or pre-canceled batch.

Cancellation reaches active queries and prevents queued work from starting.
`QueryBatchAsync` stays incomplete until all started bounded workers finish,
even if an active asynchronous plugin ignores cancellation, then propagates
cancellation. Such work cannot be forcibly terminated; the batch task remains
the observable lifetime of its eventual result/exception.
Synchronous blocking before a plugin returns a task also cannot be forcibly
interrupted. The engine imposes an external bounded timeout, continues observing
the underlying batch task and cancellation callbacks, and retains its concurrency
permit until both finish. It temporarily blocks the affected plugin entry,
counts a hang only after a five-second asynchronous cancellation grace period,
and recovers only when all cancelled work finishes. It never reuses or disposes
resources still in use by a hung call; see [polling runtime](polling-runtime.md).
The helper implements no retries, timeouts or networking. Its base disposal
owns no resources; subclasses override disposal for their own resources.
