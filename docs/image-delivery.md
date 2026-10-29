# Immutable evaluator delivery

The authoritative sequence is:

**`validate-package.ps1` → `build-image.ps1` → `validate-config.ps1` → `deploy.ps1`.**

The package/image/configuration scripts are described here;
[deployment](deployment.md) implements the final authorized Azure step.
Bicep defines resources and generated identity/telemetry settings; PowerShell
builds/pushes images. The first three scripts do not provision Azure resources.
No step creates models/entities, downloads packages at runtime or configures
storage.

## Prerequisites and trust

Use PowerShell 7.4+, the pinned .NET 10 SDK, and Docker with Linux buildx
support for image operations. Scripts fail without a working Docker daemon;
there is no simulated success, automatic daemon install, or implicit push.
No Azure login or registry credentials are needed for local builds.
An explicitly requested push uses an already authenticated Docker registry.
Local restores and online SDK builds use the root `nuget.config`, which selects
`https://packagefeedproxy.microsoft.io/nuget/v3/index.json` and clears inherited
package sources. The SDK build requires HTTPS access to that feed. If that access
is unavailable inside Docker, use the explicit trusted offline feed described
below; do not disable TLS verification.

Packages execute trusted code in-process, including during loader validation.
Only select reviewed first-party or trusted third-party **published** package
directories. Build/publish native dependencies for the target Linux architecture;
local Windows test runs do not prove Linux native compatibility.
No plugin source projects, package registries, runtime downloads or mounts
are used to acquire plugins in the final image.

### Local validation prerequisites

For the repository-wide `scripts\test.ps1` gate, use SDK **10.0.403** from
`global.json`, PowerShell **7.4+**, standalone official Bicep **0.48.1**, and
PSScriptAnalyzer **1.24.0+** on `PSModulePath`. Set `BICEP_CLI_PATH` to a trusted
executable if Bicep is not on PATH. Restore dependencies explicitly before
running the gate; it installs no tools and makes no Azure calls.

The gate builds the solution, compiles and lints Bicep and parameter files,
analyzes PowerShell, and runs every NUnit project under `tests`. Compiler stderr,
including exit-zero Bicep warnings, fails the gate without printing native
diagnostics. Skipped or empty
test runs also fail. Per-project and TRX totals and other evidence are written
beneath a fresh `artifacts\test-runs` directory. `-ResultsDirectory`,
`-Configuration`, and bounded `-CommandTimeoutSeconds` are optional.

### Package audit fingerprint

`LoadedPackage.Sha256` and `Get-EvaluatorPackageFingerprint` use the same
complete-tree SHA-256 definition. Include every file beneath the selected
`id/version` directory (manifest, dependencies, nested and hidden files).
Normalize each relative path's separators to `/`, sort paths with ordinal,
case-sensitive comparison, and form `relative/path:UPPERCASE_FILE_SHA256`
entries. Join entries with LF (`\n`), without a trailing newline, encode UTF-8
without a BOM, then SHA-256 those bytes; the result is uppercase hexadecimal.
Root paths, timestamps and enumeration/creation order are excluded; file bytes
and relative names are not. Runtime logs id, version and this hash at Information
when the catalog is loaded, even if later configuration validation fails.
Only selected/actually loaded versions are logged.

This is an audit breadcrumb, **not a package signature** or protection from
trusted code changing files. It assumes immutable image contents during load;
the host does not continuously rehash packages or attest the image/build.
Restrict build, registry push and deployment rights and retain the image digest.

## Validate a user-built package

Publish each plugin separately; its manifest must be in the publish directory.
For example:

```powershell
dotnet publish src\Plugins.Static -c Release -o artifacts\published\static
$package = & .\scripts\validate-package.ps1 `
    -PackageDirectory artifacts\published\static `
    -StagingDirectory artifacts\validated
if ($LASTEXITCODE -ne 0) { throw 'Package rejected' }
```

The script snapshots the user's publish directory into a canonical private tree
and invokes the real Host `validate-packages <packages-root>` command against
that snapshot. Loader checks include strict manifests,
abstractions compatibility, assembly paths/type identity, registration and
dependency isolation. No sink clients, secrets or tokens are created by this
loader command. Any loader failure or package modification rejects staging.
Package behavior is not exercised by this gate, so test the sink client
separately during development (see
[plugin authoring](plugin-authoring.md#test-helpers-and-local-validation)).

After the check passes, the exact bytes are copied, verified and atomically
staged at `<StagingDirectory>\<id>\<version>`. Existing versions are never
overwritten. The returned object has `Id`, `Version` and `PackageDirectory`.
The process environment is restored and only invocation-owned
scratch directories are removed. No validation receipt is a security boundary:
anyone able to modify a staged package remains trusted.

### Azure Monitor package

The managed Prometheus integration is published separately, like other sink
packages. Validate it the same way:

```powershell
dotnet publish src\Plugins.AzureMonitor -c Release -o artifacts\published\azure-monitor
$package = & .\scripts\validate-package.ps1 `
    -PackageDirectory artifacts\published\azure-monitor `
    -StagingDirectory artifacts\validated
if ($LASTEXITCODE -ne 0) { throw 'Azure Monitor package rejected' }
```

Include `$package.PackageDirectory` in `build-image.ps1 -PackageDirectories`,
together with any other selected packages. No Host project reference or Dockerfile
change is required to discover it. Configure the workspace endpoint, Entra ID
scope, and PromQL signals using the
[Azure Monitor example](configuration.md#azure-monitor-workspace-managed-prometheus).
These offline steps do not create a workspace or grant its data-access role.

## Build locally, push only explicitly

```powershell
$image = & .\scripts\build-image.ps1 -PackageDirectories @($package.PackageDirectory)
if ($LASTEXITCODE -ne 0) { throw 'Image build failed' }

# ONLY when a push is authorized and Docker is already authenticated:
# $image = & .\scripts\build-image.ps1 -PackageDirectories @($package.PackageDirectory) `
#     -Push -Registry 'example.azurecr.io' -Repository 'evaluators/worker'
```

`-PackageDirectories` requires at least one explicit published directory and
exactly one version per package ID. Pass all and only the desired first-party
and trusted third-party packages. The build rechecks the combined actual package
catalog (including cross-package auth collisions). It stages only Host/Core/
Abstractions sources plus the selected package bytes into an invocation-owned
Docker context, together with the root SDK/build properties and `nuget.config`.
Runtime configuration, tests, local build outputs and unrelated packages are not
build inputs. The NuGet configuration is copied into the SDK stage before publish,
not into the final runtime image.

`Dockerfile` publishes Host in a .NET 10.0.403 SDK stage, matching the repository's
disabled-roll-forward SDK pin. The final image uses
`aspnet:10.0-noble-chiseled`, user **1654**, `/app/host` and
`/app/packages/<id>/<version>/`. There is no shell, curl, published public port,
volume or package fetch in the runtime image. ACI probes use HTTP
(`/healthz/ready`, `/healthz/live` on 8080). Container command arrays are:

```text
entrypoint: ["/usr/share/dotnet/dotnet", "/app/host/SelfHostedEvaluators.Host.dll"]
default arguments: ["run"]
validation arguments: ["validate"]
```

`-Platform` is `linux/amd64` (default) or `linux/arm64`. Results contain:

| Property | Local default | Explicit push |
| --- | --- | --- |
| `ImageReference` | `sha256:<engine-local-image-id>` | `<registry>/<repository>@sha256:<manifest-digest>` |
| `Digest` | Engine-local SHA-256 ID, checked against `docker image inspect .Id` | Manifest SHA-256, checked remotely with `docker buildx imagetools inspect` |
| `DigestKind` | `LocalImageId` | `RegistryManifest` |
| `Platform` | Selected platform | Selected platform |

**A local image ID is immutable and runnable locally, but is not an ACI
deployment reference.** Deployment requires `DigestKind=RegistryManifest`
and a registry-qualified digest. The random upload tag is never returned as a
deployment reference. A failed/missing/mismatched digest returns no result.
Do not use a tag such as `latest` for validation, deployment or rollback.
Depending on the Docker image store, the local ID can identify the image
configuration or its local manifest. Neither form substitutes for a verified
registry-qualified deployment reference.

### Explicit offline NuGet restore

```powershell
$image = & .\scripts\build-image.ps1 -PackageDirectories @($package.PackageDirectory) `
    -OfflineNuGetDirectory artifacts\trusted-nuget
if ($LASTEXITCODE -ne 0) { throw 'Offline image build failed' }
```

Supply previously reviewed `.nupkg` archives for **all** Host/Core/Abstractions
dependencies, including transitive dependencies. A trusted local NuGet cache
can supply these archives; the script does not download or trust packages for
you. Only `.nupkg` files are staged, recursively; configuration/credentials are
excluded. Linked directories/files, empty feeds and duplicate archive filenames
are rejected. Missing dependencies fail restore, with no remote fallback.

This option sets build RUN networking to `none`, uses only the staged feed as
the restore source, and disables online NuGet audit for that restore. Review
dependency security separately; an offline restore is not a vulnerability scan.
Base images must still be cached or independently pullable. The feed is a
build-only read-only bind mount, never a runtime layer or package-download path.
Without the option, online restore uses the repository's `nuget.config` and
retains the SDK's default NuGet audit behavior.

## Validate the exact image/configuration, offline

`-Plugins` and `-Models` accept arrays of `{key, config}` objects; `config` is an
object, not a JSON string. Alternatively provide `-ConfigurationFile` with the
JSON object `{ "plugins": [...], "models": [...], "hostSettings": {...} }`.
Formatted/multiline JSON is compacted; actual embedded string newlines remain
JSON escapes. Duplicate JSON properties are rejected before projection.

```powershell
$plugins = @(@{
    key = 'STATIC'
    config = @{
        schemaVersion = 1; name = 'static-one'; package = 'static'
        endpoints = @{ default = 'https://sink.test' }; auth = @{ type = 'none' }
    }
})
$models = @(@{
    key = 'ONE'
    config = @{
        schemaVersion = 1; name = 'model-one'
        healthModel = @{ dataPlaneUri = 'https://model.test' }
        signals = @(@{
            entity = 'entity-one'; name = 'signal-one'; plugin = 'static-one'
            query = @{ kind = 'observation'; value = 42 }
            evaluation = @{ unhealthy = @{ operator = 'GreaterThan'; threshold = 99 } }
        })
    }
})
& .\scripts\validate-config.ps1 -ImageReference $image.ImageReference `
    -Plugins $plugins -Models $models -HostSettings @{ SHE_REPORTS_PER_MINUTE = 5000 }
if ($LASTEXITCODE -ne 0) { throw 'Configuration rejected; do not deploy' }
```

Exactly one `SHE_PLUGIN_<KEY>` / `SHE_MODEL_<KEY>` is emitted per entry.
Keys must match `^[A-Z][A-Z0-9_]*\z` over the complete string, with no
trailing line terminators or duplicates within a kind.
Only the seven configurable host settings in `configuration.md` are accepted.
`SHE_PACKAGES_DIR` is fixed by the image; identity/telemetry settings and reserved
prefixes cannot be overridden through `hostSettings`. Host scalars use invariant
culture and are checked for range, duration, finite numeric values and newlines.

Validation runs `docker run --rm --pull never --network none --read-only
--cap-drop ALL --security-opt no-new-privileges --env-file <private-file>
<immutable-reference> validate`. There are no host-environment pass-throughs,
volumes, shell commands, token requests, model checks or sink clients.
The exact image's Host performs full deployment validation, including all
plugin/model schemas, package versions and queries. Output from child processes
is captured, never echoed with configuration values. Errors are deliberately
payload-free. Configuration payload parameters bind as opaque objects; collection
conversion and shape validation run inside the redacted boundary, including for
malformed host settings or plugin/model collections. Explicit nulls and invalid
shapes fail rather than selecting defaults. The reusable projection still returns
an ordinal `Dictionary<string,string>`. Private scratch directories use
current-user-only Windows ACLs or Unix mode 0700, with cleanup on success/failure.

## Reusable API for deployment/test workflows

Dot-source `scripts\environment.ps1` and call:

```powershell
$environment = ConvertTo-EvaluatorEnvironment -Plugins $plugins -Models $models `
    -HostSettings $hostSettings -MaxEnvironmentBytes 32768
# Dictionary<string,string> with ordinal names; no output except that dictionary.
```

Deployment additionally passes `-GeneratedEnvironment` with only
`AZURE_CLIENT_ID`, `APPLICATIONINSIGHTS_CONNECTION_STRING` and the fixed
`SHE_PACKAGES_DIR=/app/packages`. These opaque inputs are validated under the
same redacted boundary, included in the full byte budget and passed to
`validate-config.ps1` for exact-image offline validation. They are never
permitted as user `hostSettings`.

`ConvertFrom-EvaluatorConfigurationJson -Json <text>` performs strict
duplicate-property checks before producing an input dictionary.
`ConvertTo-EvaluatorEnvironment` checks input shape/keys/host scalars and bytes;
**it does not replace** exact-image validation of plugin/model semantics.

`MaxEnvironmentBytes` defaults to a **tool safety budget** of 32768 UTF-8 bytes
for all serialized `name=value\0` entries plus a final terminator. This is not
a claim of a universal ACI/ARM/Linux limit. Supply a smaller verified target
budget when required. Deployment measures the **complete**
environment after generated `AZURE_CLIENT_ID` / Application Insights values,
and checks conservative ARM request/resource budgets, failing explicitly rather than
chunking, logging payloads or introducing a storage fallback. Bicep supplies
those generated settings separately, never via user `hostSettings`.

Configuration changes reuse the same image and go through `validate-config`
before redeployment. Package/code changes rebuild the image, producing a new
content digest. Rollback uses the previous **registry manifest digest and
complete environment**. `deploy.ps1` supplies that orchestration;
it preserves the package validation workflow rather than replacing it.

Shared native-tool execution is bounded to 900 seconds by default and captures
stdout separately from stderr. Failures/timeouts never print raw tool output.
The deadline kills only the invocation-owned process tree; deployment uses
smaller bounds for state inspection and remote probes.

## Verification boundary

`scripts\test.ps1` builds the solution, compiles Bicep and runs the NUnit
projects under `tests`. The image, deployment, teardown and verification scripts
have PSScriptAnalyzer coverage but no automated behavior tests, so exercise them
in a non-production environment before relying on them. Core tests cover package
loading, isolation and version rejection. The container gate below is the only
check that builds and runs the image.

### Actual local container acceptance

```powershell
& .\scripts\test-container.ps1
# With a trusted prepopulated feed when Docker cannot reach NuGet:
# & .\scripts\test-container.ps1 -OfflineNuGetDirectory artifacts\trusted-nuget
if ($LASTEXITCODE -ne 0) { throw 'Container acceptance failed' }
```

This separate opt-in gate requires a running **Linux amd64 Docker daemon**, the
pinned SDK, and local base images or permission/connectivity to pull them.
It builds the actual Dockerfile with all three first-party packages, repeats
identical inputs, builds a Static-only image, and recompiles a changed package.
It checks package loading, configuration validation without networking,
selected-package rejection, realistic environment size, immutable image-ID
changes, non-root/no-shell layout, and real HTTP ready/live probes.

The production chiseled worker runs unchanged on an invocation-owned **internal**
Docker network, with no published ports, package/config mounts or external
service traffic. Test-only SDK containers supply managed identity, vendor HTTPS
and model-ingestion fixtures. A short-lived fixture CA is mounted read-only;
production certificate validation is not bypassed. These are not real Azure
identity or authoritative model readback fixtures.

The gate exercises multi-plugin/multi-model reporting, NoData/failure Unknown
states, warn-only model checks returning HTTP 404, real 429/503 retries with
stable report IDs/bodies, configuration-only
replacement, package-image replacement and rollback. Real Linux SIGTERM fails
readiness and exits cleanly, including during a flushed partial SSE response
with the five-second shutdown budget. SIGKILL is distinguished from graceful
stop. Stalling PID 1 makes the actual HTTP liveness probe fail; an **explicit local
Docker restart** restores it. This does not prove ACI's automatic restart policy.

Evidence (`acceptance.json`, request snapshots, image metadata and container
logs/state) is retained under a fresh `artifacts\container-tests\<run-id>` directory
or `-ResultsDirectory`. The gate removes only its own containers/network and
new result images, preserving preexisting images. No global pruning or Docker
setting changes occur. Certificates/configuration in the evidence directory
belong exclusively to local fake services; never substitute real secrets.

This gate has passed on Linux/amd64. An additional `linux/arm64` emulated image
build/load/readiness/shutdown check passed, but a cold liveness probe
intermittently failed under its existing two-second HTTP budget in emulation. Native
amd64 clients reached that same server successfully. **Native ARM64 acceptance
is not established**; do not infer it from emulation or loosen production probe
timeouts to conceal the result. The ACI deployment tooling targets amd64.

The platform-neutral Contracts/Core/Host/plugin suites have also run
successfully in a network-disabled Linux SDK
container, without a developer HTTPS certificate. The plugin test helpers generate
and pin a per-fixture certificate instead of depending on machine-wide trust.
Neither gate proves registry push, managed-identity image pull, cloud routing,
live TTL/recalculation, telemetry ingestion or alert delivery.
