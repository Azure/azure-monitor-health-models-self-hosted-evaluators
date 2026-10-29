# Authentication runtime

Core's `SelfHostedEvaluators.Core.Auth` namespace implements the typed profiles
in `Core.Configuration`. The plugin contract is unchanged. No plugin instance
context exposes a secret resolver, credential or token getter.

## Composition and public APIs

Compose these as host services after validating the complete snapshot/catalog:

1. One `Redactor` and `TimeProvider`.
2. One disposable `KeyVaultSecretSource(host.AzureClientId)` using the attached
   bootstrap UAMI, not a data-plane identity. The constructor requires its client
   ID. The injectable `IKeyVaultSource`/`TokenCredential`/`IHttpTransportFactory`
   boundaries support offline tests.
3. One `SecretResolver(source, host.SecretCacheTtl, time, redactor, logger)`.
4. One `AzureCredentialFactory(resolver, new AzureSdkCredentialSource(), time,
   redactor)`, and one `AuthRegistry(resolver, credentials, redactor, time,
   transportFactory, logger)`.
5. Call `registry.RegisterPackageSchemes(catalog.AuthSchemes, activate)` once.
   `activate` is an optional host/DI type activator. Registration is
   case-insensitive and rejects built-in/previous scheme names.
6. Inject `PluginContextFactory(catalog, registry, redactor, logger, time, diagnostics)` into
   `PluginManager(catalog, contextFactory)`.

`SecretResolver.ResolveAsync(Uri, CancellationToken)` also implements the
construction-only `ISecretResolver.ResolveSecretAsync`. `KeyVaultReference.Parse`
accepts only `kv://vault/name[/version]`, never user info, ports, queries,
fragments or encoded path separators. Cache fills are coalesced per reference;
TTL expiry re-reads the source, and failed refresh never returns expired data.
Both old and new values stay registered for redaction of late diagnostics.

Certificates are exportable Key Vault certificates retrieved through their
backing **secret**, retaining the specified name/version. Passwordless base64
PFX and PEM containing the certificate and private key are supported. Imports
are in memory, PFX keys are ephemeral, and missing private keys fail explicitly.
Non-exportable/HSM private keys are not supported by client TLS or this
certificate credential API.

`AzureCredentialFactory.Create(profile, limiter?, timeout?)` returns an owned
refreshing `TokenCredential`; `CreateAsync(profile, ct, limiter?, timeout?)`
also preloads secret/certificate material. Dispose its `IAsyncDisposable`
implementation after all token requests finish. It selects only explicit
managed identity, workload identity, client-secret or client-certificate
credentials (`IAzureCredentialSource` is injectable). There is no default,
interactive, CLI or environment credential chain. Workload token files must
already exist externally; nothing provisions or writes one.

The credential refreshes Key Vault material on every token acquisition path,
invalidates cached tokens on rotation and caches by scopes/claims/tenant/CAE.
Models must each get their own credential/context and pass their configured
token scope. `entraId` instead uses its profile's scope.

`AuthRegistry.CreateContextAsync(profile, endpoints, limiter, timeout, ct,
azureScope?, allowedHosts?)` creates host-owned auth operations.
`CreateClientAsync(profile, endpoint, limiter, timeout, ct, azureScope?)`
creates an owning standalone **sink** client with automatic retries/limiting.
`CreateHandlerAsync(profile, endpoint, ct)` supplies the brief's standalone
owning delegating-handler API, with a 30-second/60-requests-per-second default.
Disposing these standalone clients/handlers disposes their auth contexts;
an externally supplied limiter remains the caller's property.

Use `CreateSingleAttemptClientAsync(profile, endpoint, timeout, ct,
azureScope?)` for reporting. Its data-plane transport has no hidden retries or
sink permits and returns non-followed redirects as statuses. Reporting uses
header-first completion; model checks process status/Retry-After before buffering
successful entity content with a 64 KiB bound. Unneeded GET error bodies are
disposed unread. The original timeout and transport/certificate ownership remain
with streamed content until it is disposed, even across rotation or client
disposal. Core's publisher opts out of reading unneeded report
response bodies so their contents cannot override acceptance/error statuses.
It owns a separate 60/minute auth-exchange limiter and
its auth context. Reporting's `ModelClient` owns every model request permit,
including retries and GET checks. Do not substitute the sink client here;
see [reporting runtime](reporting-runtime.md) for Host composition/ownership.

## Schemes and guarded transports

- `none`, header/query `apiKey`, `bearer` and `basic` use per-request operations;
  secret TTL refresh works without rebuilding the plugin.
- `oauth2ClientCredentials` posts form-encoded client credentials/scopes to the
  explicit token endpoint. It requires a valid Bearer token and positive
  `expires_in`, caches to expiry and invalidates on client-secret rotation.
- `mtls` refreshes certificate material before each send, including on an
  existing `HttpClient`. Transport generations change with certificate identity.
  Retired transports stay alive until their in-flight response bodies finish.
- `entraId` and direct Azure profiles apply tokens from the explicit credential.
- Package schemes receive cloned profile JSON, resolving operations, a
  redacting logger and a guarded, bounded, rate-limited exchange client. Their
  optional `tokenEndpoint` must be explicit HTTPS; otherwise exchanges are
  restricted to the plugin's configured origins. Package code remains trusted,
  not sandboxed.

Every sink client is restricted to its exact configured HTTPS origin, including
port. Its shared auth context permits only configured origins, with package
host allow-lists additionally checked for both sinks and user-configured
OAuth/custom token endpoints. Cross-host token endpoints must be explicitly
allowlisted when the package configures an allow-list. HTTP/user-info/fragment
destinations and Host-header overrides fail before credentials leave the process. Redirects
(including same-origin redirects) are disabled and any 3xx response fails
explicitly. Cookies, ambient credentials and preauthentication are disabled.

HTTP requests and auth exchanges get at most three attempts for 429/5xx,
bounded by the supplied timeout/caller cancellation. Both Retry-After formats
are honored; absent headers use capped jittered backoff. Each sink attempt
acquires one request permit before applying credentials, so queued requests
refresh expired tokens after the limiter wait. Auth exchanges independently
consume their own permits, not a second permit for the sink request. A 429
pauses the plugin's shared limiter even when the response exhausts retries.
Sink HTTP and OAuth/custom exchange waits inherit the effective poll deadline,
including time already spent queued or activating. A retry delay that cannot
fit the remaining budget returns the typed HTTP status without retrying; a
limiter pause/refill that cannot fit fails promptly with `request_rate_limited`.
The 429 pause still applies to later requests. This is not a hung plugin.
Native Azure SDK exchanges use the
same effective poll deadline, budget-aware retries and plugin limiter; SDK retries
are disabled to avoid multiplicative retries. Key Vault and its bootstrap token
exchanges use a separate host-owned 60/minute budget and a 45-second overall
Key Vault call budget. The SDK controls the local managed-identity endpoint;
sink origin policy is not applied to Azure's identity-metadata protocol.

`AuthenticateWebSocketAsync` accepts only WSS equivalents of configured HTTPS
origins/ports. It refreshes headers/certificates. A plugin must acquire **one
request lease** from `context.RateLimiter` per non-HTTP outbound operation,
including reconnects, and must disable credential-bearing redirects in its own
transport. Query-parameter WebSocket auth fails explicitly: the public
options-only auth operation cannot rewrite its destination URI. HTTP query auth
is fully supported. No operation silently falls back to anonymous access.

## Ownership and polling integration

`PluginRequestLimiter(RateLimitOptions, time?)` is a request token bucket
derived from the public `RateLimiter` type. `AcquireAsync(1, ct)` consumes a
request; disposing a lease does **not** replenish tokens. `Pause(TimeSpan)` is
plugin-local. It is shared by all endpoint clients and auth exchanges for that
plugin, and exposed for non-HTTP transports.

**Do not acquire a request permit per batch.** The independent
batch-concurrency semaphore and external effective-interval timeout are
implemented by `Polling.PollExecutor` (see [polling runtime](polling-runtime.md)).
Pass
that cancellation token to the actual batch/client requests; the configured
HTTP timeout is only an additional upper bound. A timeout must not release the
batch-concurrency permit while the underlying batch is still running.

The context factory owns endpoint clients, auth/exchange resources and limiter.
`PluginContextLease.Own(ISinkClient)` transfers a successful activation to an
owning client wrapper (once only). `PluginManager` uses this path, disposes
resources on activation failure/cancellation, and waits for all underlying
`QueryBatchAsync` tasks before disposing sink/context resources. Disposal can
therefore wait indefinitely for an uncooperative plugin. The executor temporarily
blocks that entry and retains ownership through actual task/callback completion;
hangs are counted after five seconds of cancellation grace, and recovery waits
for all unfinished cancelled work. It never disposes/reuses those resources
early. The delegate-based `PluginManager` constructor is retained for
callers that explicitly own their own context resources, not production DI.

Certificate generations used by public non-HTTP auth operations are retained
until context disposal because that contract has no end-of-handshake lease.
This intentionally protects ongoing sockets/batches during rotation.

## Diagnostics and host/exporter integration

`Redactor.RegisterSecret` is host/construction-only; it registers original,
URL-encoded and JSON-escaped representations. Complete registered values are
removed before destructive header/query rewriting; raw credential header
values are masked through commas and semicolons. Auth code also registers Basic
encodings and acquired tokens. `RegisterHeader` covers custom API-key names.
`Redact` strips credential headers and URL queries for diagnostic text and
`additionalContext`. Apply it **before** truncating rendered report context.

`Wrap(ILogger)` redacts formatted messages/scopes at every level, uses structured
query/request-target/header keys to mask their values before keys are lost, and
preserves sanitized structured fields and never forwards raw exception objects. Secret/auth/transport failures
surface safe codes rather than upstream bodies, exception messages or secret
references. The built-in `SelfHostedEvaluators.Core.Auth` activity source emits
only origin/status tags, not headers, bodies or query strings.

Host's `SafeLoggerFactory` applies the wrapper to Host, framework and plugin
loggers. SDK/HTTP instrumentation disables raw exception recording.
`RedactActivity(Activity)` scrubs display names, mutable string tags, header tags,
standalone query/request-target attributes (including `url.query` and
`http.target`), baggage, trace state and status descriptions before export.
Relative HTTP request-target queries are also removed from diagnostic text.
The Host trace processor fails closed (drops the span) when immutable
operation/source metadata, events or links cannot be safely rewritten; it never
manufactures new correlation IDs. No raw exception events are exported.
See [host runtime](host-runtime.md) for explicit UAMI exporter authentication,
disabled auxiliary telemetry and injected offline exporter tests.
`Evaluation.Evaluator` implements report-context rendering,
redaction-before-truncation and typed evaluation output; report building and
publishing are implemented by Core.Reporting; see the reporting runtime guide.
