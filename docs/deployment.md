# ACI deployment and acceptance

Start with the [overview and setup steps](../README.md#set-up-an-evaluator).
The [design](../design-spec.md) and [system diagram](../system-diagram.md)
describe the architecture. PowerShell builds/pushes images and orchestrates safe
phases; Bicep declares Azure resources and all role assignments.

**Running `deploy.ps1` is a real infrastructure change and can push images.**
Obtain authorization for the subscription, registry and any interruption first.
Repository verification does not authorize deployment. No live Azure what-if,
provisioning, image push, secret retrieval or model/sink traffic was performed
as part of implementation. The scenario matrix below is a runbook, not a claim
that live scenarios have been executed.

## Tooling and inputs

Use PowerShell 7.4+, the pinned .NET SDK, Linux Docker with buildx, Azure CLI
authenticated to the intended **AzureCloud** subscription, and standalone
Bicep. Local validation uses official Bicep **0.48.1**. Set `BICEP_CLI_PATH` or pass
`-BicepPath` to use a non-PATH executable; no machine-specific path is embedded
in the repository. Docker must actually work: there is no fallback runtime,
simulated image build or automatic installation.

Start with `infra\main.bicepparam`, replacing the example existing model
resource ID, HTTPS data-plane URI, entities, signals and package configuration.
The zero subscription ID intentionally fails deployment validation. Models and
entities must already exist; this infrastructure never creates them.
See [configuration](configuration.md), [authentication](auth-runtime.md), and
[image delivery](image-delivery.md) for the complete contracts.

Compile parameters locally into an ignored artifact directory:

```powershell
# Replace this with a trusted local executable path when it is not on PATH.
$bicep = if ($env:BICEP_CLI_PATH) { $env:BICEP_CLI_PATH } else { 'bicep' }
New-Item -ItemType Directory -Force artifacts | Out-Null
& $bicep build infra\main.bicep --outfile artifacts\main.json
if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed' }
& $bicep build-params infra\main.bicepparam --outfile artifacts\desired.parameters.json
if ($LASTEXITCODE -ne 0) { throw 'Parameter compilation failed' }
```

`-ParametersFile` accepts ARM JSON with inline `{ "value": ... }` parameter
entries, not `.bicepparam` source or ARM secret-reference entries. Alternatively,
`-Parameters` accepts an opaque dictionary of Bicep parameter names to values.
Unknown parameters, missing required inputs, duplicate JSON properties and
invalid collection shapes fail closed behind the redacted error boundary.
Keep these inputs non-secret. Credential fields and API-key header values must
be `kv://vault/name[/version]` references; do not put credential values in
configuration, command arguments, source control or ARM deployment history.
Application Insights' generated connection string is telemetry configuration,
not an alternative to authenticated ingestion.

Use complete desired configuration on every invocation. Defaults are read
from the compiled template, merged once, and retained throughout deployment.
Configuration is immutable at process startup; omitted entries are not a
partial-update request. The script controls `deployContainerGroup` and
`imageDigest` for the two resource phases.

### Deployment audit revision

The successful `Deployment ready` line records the immutable registry manifest
reference and `parameter-source sha256:<uppercase hex>`. Retain that line with
the approved release inputs. The revision is SHA-256 of UTF-8 (no BOM)
compressed canonical JSON of the caller's complete inline parameter **values**,
snapshotted before template defaults, generated outputs or deployment-phase
mutations. Dictionary keys are recursively sorted ordinal/case-sensitive;
array order, nulls and value types are preserved using `System.Text.Json`'s
default JSON escaping, without indentation or a trailing newline.
The ARM parameter-file wrapper,
file path, indentation, key insertion order and platform newline convention are
excluded. Timestamp-like strings stay strings (no local-time conversion);
JSON numbers retain their source numeric spelling. Equivalent file/object
inputs with the same numeric spelling have the same revision; changing
configuration values changes it without implying any image change. The
separately recorded image reference identifies package/code bytes.

Only the hash is logged, never parameter JSON, secret values/references or
source paths. This is a content revision, not a git revision, signature,
authorization decision or attestation of Azure's effective deployed state.
Template/default changes still require a trusted source release record;
generated identity/telemetry values are not part of this source-content hash.

## Permissions and secret bootstrap

The operator needs permission to create subscription/resource-group deployments,
the declared resources, and to assign built-in roles at each actual
target scope. Existing models may be in different resource groups/subscriptions;
the operator must have deployment/role-assignment permission there too. Register
the relevant Azure resource providers through your normal authorized process.
`az acr login` also requires appropriate operator registry access; AcrPull for
the evaluator identity does not authorize the operator to push.

Bicep attaches one user-assigned identity and grants only:

| Scope | Identity access |
| --- | --- |
| Registry | AcrPull |
| Key Vault | Key Vault Secrets User and Key Vault Certificate User |
| Application Insights component | Monitoring Metrics Publisher |
| Each named, existing health model | Built-in Contributor |

Model reporting requires
`Microsoft.CloudHealth/healthmodels/read`,
`Microsoft.CloudHealth/healthmodels/entities/read`, and
`Microsoft.CloudHealth/healthmodels/entities/ingestHealthReport/action`.
These are regular actions, not data actions. Built-in Contributor
(`b24988ac-6180-42a0-ab88-20f7382dd24c`) supplies them without creating a custom
role. It also permits modifying/deleting the assigned model and its children;
this is an accepted permission expansion beyond report submission.
Assignments are **only model-scoped**, never resource-group- or
subscription-wide. Cross-subscription models use that model subscription's
built-in role definition. Monitoring Contributor does not include the ingest
action and is not a substitute. `adminPrincipalIds` receive Key Vault Secrets Officer on this
vault, not general infrastructure administration or evaluator identity access.
No storage permissions or workspace shared keys are used.

For the `azure-monitor` managed Prometheus plugin, separately grant
**Monitoring Data Reader** on the existing Azure Monitor workspace to the
identity configured in the plugin's `auth.azure` profile. The templates do not
create that workspace, configure ingestion, or assign this source-specific role.
Use its Query endpoint with the
`https://prometheus.monitor.azure.com/.default` scope; see the
[configuration example](configuration.md#azure-monitor-workspace-managed-prometheus).
Ensure outbound HTTPS and DNS reach that endpoint. If workspace public access
is disabled, arrange its private connectivity separately; the network modes
below only provision private endpoints for ACR and Key Vault.

The vault uses RBAC, soft delete and purge protection. Secret/certificate
values are never provisioned by these templates or scripts. An initial rollout
can create the vault and fail readiness until an authorized operator populates
it through approved secret-management tooling. Keep the rollout's ownership
manifest, populate that vault, then rerun with the same manifest and complete
desired parameters. The orchestration does not pause to collect secrets.

A direct Bicep deployment with `deployContainerGroup=false` is also possible,
but it produces no rollout ownership manifest: the updated rollout will not
adopt its resources, and automated teardown will refuse it. Do not use that
bootstrap path when manifest-managed rollout and teardown are required.

## Network modes

| Mode | Resources / reachability |
| --- | --- |
| `public` | No VNet/NAT. Supporting ACR/Key Vault use public endpoints; evaluator still has no public IP or inbound ports. |
| `vnetNat` | Dedicated ACI subnet delegated to `Microsoft.ContainerInstance/containerGroups`; NAT Gateway with Standard static public IP provides outbound access. ACR/Key Vault retain public endpoints. |
| `private` | Same delegated ACI subnet and NAT, plus a separate nondelegated endpoint subnet, Premium ACR, ACR/Key Vault private endpoints and linked private DNS zones. Public access to both supporting services is disabled; ACR trusted-services bypass remains enabled for managed-identity image pull. |

The optional desired parameter `keyVaultPublicNetworkAccess` accepts `Enabled`
(default) or `Disabled`. In `public` / `vnetNat` mode, set it to `Disabled`
when subscription policy forbids vault public access and this deployment does
not need that vault's data plane. The vault's default network ACL then denies
access, with no trusted-services bypass. This setting does not change ACI or
ACR networking, create a private endpoint, or grant private reachability.
Without separately arranged private connectivity, that vault cannot serve
`kv://` secret/certificate references or secret bootstrap, including during
local image validation. Managed-identity-only configurations that do not use
the vault can leave it inaccessible. `private` mode always disables vault
public access regardless of this parameter, preserving its existing endpoints
and operator-reachability requirements. Omitted parameters preserve all
existing network-mode defaults.

Defaults are VNet `10.42.0.0/16`, ACI `10.42.0.0/24`, and endpoints
`10.42.1.0/24`. ACI requires an aligned `/24` **or larger** dedicated subnet
(prefix length at most 24). Subnets must be contained in the VNet and must not
overlap. Do not place other resource types in the delegated subnet.
Private DNS zones are `privatelink.azurecr.io` and
`privatelink.vaultcore.azure.net`; no storage endpoint or zone exists.
VNet-attached groups explicitly use Azure DNS `168.63.129.16`.

Private-mode operators must arrange routing and private DNS resolution to the
new VNet for build/push and secret administration, and pass
`-PrivateNetworkReachable` as an explicit attestation. That switch does not
provision VPN, peering, DNS forwarding or an operator machine. Registry
operations fail if reachability is absent. NAT still allows required outbound
sink, model, identity and Azure Monitor traffic; `private` does not mean every
external dependency is privately connected.

The script rejects sovereign clouds and unsupported region names before
provisioning. The conservative compatibility allow-list is `eastus`, `eastus2`,
`westus2`, `westus3`, `centralus`, `northeurope`, `westeurope`, `uksouth`,
`australiaeast`, `southeastasia`, `japaneast`; it additionally checks the
subscription's ACI provider locations. Availability, capacity, zone support
and quotas can still cause an explicit ARM failure. This is not a promise of
capacity in every listed region.

## Delivery and safe deployment phases

Preserve the upstream **`validate-package.ps1`** gate for each reviewed,
published package. Its real loader check precedes
`build-image`/deployment; deployment does not pretend a path is a package
validation receipt. See [image delivery](image-delivery.md) for staging.

After authorization, supply the entire selected package set:

```powershell
# These commands mutate Azure and push images; do not run without authorization.
# $subscription = '<authorized-subscription-guid>'
# & .\scripts\deploy.ps1 -SubscriptionId $subscription `
#     -ParametersFile artifacts\desired.parameters.json `
#     -PackageDirectories @($package.PackageDirectory) -Confirm
# if ($LASTEXITCODE -ne 0) { throw 'Deployment rejected or not ready' }
```

For configuration-only redeployment or rollback, supply the previous
registry-qualified **manifest** reference with `-ImageReference` instead of
`-PackageDirectories`. It must match the desired registry/repository:
`<registry>.azurecr.io/<repository>@sha256:<64 lowercase hex>`. Local image
config IDs (`DigestKind=LocalImageId`), mutable tags, mismatched registries,
digest mismatches and non-Linux/amd64 images cannot reach ACI. Builds explicitly
push with `DigestKind=RegistryManifest`; ACI receives the digest, never the
random temporary upload tag.

The workflow is:

1. Snapshot/validate opaque inputs and non-secret environment projection;
   compile Bicep, merge defaults, check sizes/network/resources/cloud/region.
2. Run subscription Azure what-if with resource-ID-only output. Raw tool
   output, property diffs and configuration are captured, not echoed.
3. After `ShouldProcess`, deploy Bicep prerequisites with compute disabled.
   Incremental deployments leave an existing ACI group untouched.
4. Authenticate to ACR; build/push reviewed packages, or verify a reused
   manifest remotely. Pull the exact digest and inspect Linux/amd64 architecture.
5. Add generated identity/telemetry/package-path values, validate the complete
   environment budget, and run the exact image's offline `validate` command.
   It has no network and cannot resolve secrets or call sinks/models.
6. Write the complete final desired state and run a second, validated what-if.
   Query assignments at the registry resource scope (not subscription-wide
   `--all`) and wait boundedly for the exact UAMI AcrPull assignment to become
   visible. Only successful array responses without that assignment retry;
   CLI exit errors, authentication/permission errors and invalid responses
   fail immediately with sanitized phase guidance. Check CLI syntax,
   authentication and registry role-assignment read permission before rerunning.
7. Inspect any existing group. Compare region, SKU, container count, CPU,
   memory, OS, restart policy, zones, subnet and owned network-mode marker.
   A compatible update submits the **entire** validated template/configuration.
8. Incompatible settings require **both** `-ConfirmRecreate` and a successful
   destructive `ShouldProcess` decision. Without them, fail before deletion.
   With them, delete only the intended group and recreate it. Reporting and
   in-memory work are interrupted; this is not a rolling revision.
9. Deploy the complete group, retrying only recognized image-pull failures
   within the RBAC budget, including rejection before the resource exists.
   Failed ARM provisioning is an error even if a CLI command exits zero.
10. Poll ARM and the container's instance view until provisioning has Succeeded
    and the evaluator is Running. Any restart fails the deployment. Transient
    `Readiness|Liveness probe failed:` events are tolerated while the container
    is Running with zero restarts. No in-container probe is executed. Raw
    lifecycle messages are not logged because they can contain sensitive
    application output.

Configuration rejection occurs before existing-group update/deletion, including
when `-ConfirmRecreate` was requested. Prerequisite changes and a newly pushed
image are not automatically rolled back when a later phase fails. An interrupted
delete/recreate can leave no running worker; rerun with the same complete
desired state after investigating. Azure failures are not converted to success.

PowerShell `-WhatIf` is deliberately a **local no-side-effect preview** of
the orchestration, not execution of Azure what-if or full semantic validation.
Every real run performs both Azure what-if gates. Use authorized Azure tooling
separately if an operator needs to review resource-ID-only changes before
approving the workflow; never publish property-level configuration diffs.

Native tools have an owned-process-tree deadline (900 seconds by default);
state/role inspections use 60 seconds.
Defaults are six RBAC/pull attempts, thirty readiness attempts and a fifteen
second retry delay. `-RbacAttempts`, `-ReadinessAttempts` and
`-RetryDelaySeconds` are bounded knobs, not unbounded polling. Failures report
the safe phase, never native stdout/stderr, raw exception payloads, config,
tokens or telemetry connection strings. Inspect Azure deployment/event details
only through an authorized, appropriately protected operator channel.

## Teardown

`scripts\teardown.ps1` stops evaluator containers and removes infrastructure
created by the updated `scripts\deploy.ps1`. **This deletes registry images,
vault contents, monitoring resources and evaluator access.** Obtain explicit
authorization before running it. Existing health models and action groups are
never deletion targets.

The rollout writes a non-secret ownership manifest before provisioning and
extends it before the container phase. By default it is
`artifacts\deployments\<subscription>-<resource-group>-<name-prefix>.json`;
`deploy.ps1 -ManifestPath <path>` selects another location. Retain and back up
this file even after a failed or interrupted rollout. Reuse it on subsequent
rollouts; do not edit ownership fields, delete it to bypass a collision, or
check it into source control. It contains exact planned resource IDs and API
versions, the ownership ID, model scopes, and whether the group existed before
rollout; it contains no evaluator configuration or credentials.

The `she-owner` tag and `she-network-mode` tag are reserved; callers may supply
at most 48 other tags. Existing resources must already belong to the same
manifest. Name matches, user tags and old deployment names are not proof of
ownership. **Deployments predating the ownership manifest are not supported
by automated teardown and are never automatically adopted.**

```powershell
$manifest = 'artifacts\deployments\<subscription>-<resource-group>-<name-prefix>.json'

# Local preview only: no Azure/Docker calls, writes, stops or deletions.
& .\scripts\teardown.ps1 -ManifestPath $manifest -WhatIf

# Run only after authorization; destructive operations prompt for confirmation.
& .\scripts\teardown.ps1 -ManifestPath $manifest
if ($LASTEXITCODE -ne 0) { throw 'Teardown incomplete; retain the manifest and inspect resource state' }
```

Real teardown validates the subscription and all present deletion targets
before any destructive action. It stops/removes only local validation
containers bearing the exact `she.rollout=<ownership-id>` label, then stops
and removes the evaluator's Azure container group. Run on the rollout machine
with the same Docker context; Docker is required if local validation was
started. Other applications, acceptance-test containers, Docker images/build
caches and the Docker service itself are not pruned or stopped.

Azure resources are deleted individually in dependency order, including
private networking, resource-scoped assignments, the reporter assignment on
each existing external model, and owned
deployment-history records. Generated private-endpoint NICs and DNS records
are recognized through their owned parent associations, not name guesses.
Foreign child resources block cascade deletion of an owned parent.

Built-in role definitions are shared and are never created, journaled as owned,
or deleted by a new rollout. Existing manifests retain cleanup support for
previously created custom roles, but deletion requires both `type=CustomRole`
and the exact rollout ownership description. Updating an existing rollout
retains its old manifest entries so legacy assignments/roles remain safely
discoverable for teardown.

The resource group is deleted only if the manifest records that rollout
created it, its live ownership tag still matches, and both the initial and
final inventories contain no unrelated resources. Otherwise the group is
retained with an explanation. The inventory includes resource-group deployment
records, role assignments and declared networking children, not just tagged
top-level resources. Pre-existing groups are always retained, even when empty.
Do not run concurrent rollouts, teardown, or other resource writers: Azure
does not provide an atomic delete-resource-group-only-if-empty operation.

Missing/malformed manifests, ownership mismatches, failed/incomplete inventories,
permission failures and failed stops/deletions fail closed with a nonzero exit
code. Already-absent resources are skipped; retain the manifest and rerun after
resolving a partial failure. The manifest remains as recovery/audit evidence.
Teardown does not purge soft-deleted resources or bypass Key Vault's 90-day
purge protection. Azure retention and protected name reservations can therefore
outlive teardown.

## Runtime, limits and rollback

The only worker is Linux ACI **Standard**, `restartPolicy: Always`, stable API
**2025-09-01**, attached UAMI and no public `ipAddress`, exposed ports, mounts,
storage, sidecar, runtime package download or ARM deployment-script resource.
Image pull uses the **identity resource ID**, not its client ID. Generated
`AZURE_CLIENT_ID` selects that identity at runtime; authenticated Application
Insights ingestion uses its component-scoped role.

`SHE_PLUGIN_<KEY>` and `SHE_MODEL_<KEY>` each hold one compact JSON object.
Bicep generates identity/telemetry values and fixes `SHE_PACKAGES_DIR` to
`/app/packages`; user host settings cannot override these. The reusable
ordinal environment projection accepts these generated values only through
its separate `GeneratedEnvironment` argument. Its default 32768-byte safety
budget includes **all** `name=value\0` entries and final terminator, including
generated values. `-MaxEnvironmentBytes` can lower, not bypass, this deployment
ceiling. Oversized configuration fails; it is never chunked or uploaded.

Serialized parameters plus the compiled template are conservatively capped at
3 MiB, below ARM's documented 4 MiB request/parameter limits. The environment,
resource naming/tag limits and 500 model/admin-assignment budget keep expanded
individual resource bodies below ARM's 1 MiB resource limit. These safeguards
are not a statement that ACI accepts all combinations of CPU/memory or every
host environment size. ARM remains authoritative for regional compute limits.

Both probes are ACI `httpGet` probes against container port 8080:

```text
readiness: GET http://:8080/healthz/ready
liveness:  GET http://:8080/healthz/live
```

Probe defaults are
readiness delay 5s, liveness delay 60s, period 10s, timeout 5s and failure
threshold 3. CPU/memory, delays, period, timeout, threshold and optional zone
are parameters. Timeout cannot be below 3s. No unsupported startup probe or configurable ACI termination-grace
claim is made. See [host runtime](host-runtime.md) for best-effort shutdown.

Rollback is a new deployment of the previous **registry manifest digest plus
complete prior configuration**, using the same exact-image validation and
compatibility gates. Retain those inputs in an approved non-secret release
record. No report queue or in-memory progress survives replacement.

## Local verification and remaining boundary

```powershell
dotnet restore SelfHostedEvaluators.slnx
& .\scripts\test.ps1
if ($LASTEXITCODE -ne 0) { throw 'Offline validation failed' }
```

`test.ps1` compiles `infra\main.bicep` and `infra\main.bicepparam` with real
Bicep and fails on any compiler warning. There are no automated template
assertions or deployment-script behavior tests, so review the `what-if` output
and exercise phases, parameter reuse, digest enforcement and recreation
confirmation in a non-production environment before relying on them.

Run PSScriptAnalyzer against `scripts` and real `bicep build`/`build-params`
before shipping (`test.ps1` includes both, plus every test project). It requires
PSScriptAnalyzer 1.24.0+ on `PSModulePath`; it never installs tools and rejects
missing/empty/skipped/failing TRX evidence. There is no CI/CD in this task.

The separate `scripts\test-container.ps1` gate has passed with the actual
Linux/amd64 Dockerfile: selected-package builds, startup/HTTP probes, local fake
identity/vendor/model reporting, configuration/image replacement, rollback,
active-SSE SIGTERM and explicit local restart. It creates only task-owned local
Docker resources and does not execute Azure commands. See
[container acceptance](image-delivery.md#actual-local-container-acceptance) for
its command, offline-feed option and ARM64-emulation limitation.

Live Azure schema/service acceptance, private routing/DNS, managed-identity
image pull, RBAC propagation, rotation, telemetry queries
remain unverified until an authorized deployment. Local explicit Docker restart
does not establish ACI automatic probe/restart behavior.

## Authorized readback

`scripts\verify-e2e.ps1` performs **read-only** HTTP GETs after an independently
authorized deployment. It never deploys, writes health reports, changes secrets,
restarts a container or provisions entities. `-AuthorizeReadback` is required
before acquiring credentials or sending any request. Do not use it against a
production model without authorization.

### CloudHealth diagnostics and ARM status

Use **`-CloudHealthDiagnostics`** for the reviewed real service contracts. Each
model must explicitly declare its actual ARM `resourceId`, root `dataPlaneUri`,
data-plane `apiVersion` and `controlPlaneApiVersion`. No endpoint is inferred
from a model name, workspace endpoint or ARM resource ID. Obtain the verified
endpoint/model pairing from the model's read-only `properties.dataPlaneUri` at
ARM api-version `2026-12-01-preview` (served in INT), or through the service
owner or trusted model setup where the stamp does not serve that version.
`2026-10-01-preview` and earlier ARM versions do not expose it.

The verifier reads **both** views of every entity:

```text
GET {dataPlaneUri}/api/entities/{entityName}?api-version={apiVersion}
Audience: https://data.cloudhealth.azure.com

GET https://management.azure.com{resourceId}/entities/{entityName}?api-version={controlPlaneApiVersion}
Audience: https://management.azure.com
```

Only data-plane **`2026-06-01-preview`** and **`2026-10-01-preview`** are supported;
the declared control-plane version must be **`2026-10-01-preview`**. The schema
authorities are the service's versioned data-plane generated OpenAPI
(`Monitor.HealthIntelligence.DataPlane.ApiModel\generated\openapi\<version>\openapi.json`)
and control-plane TypeSpec (`Monitor.HealthIntelligence.ControlPlane.ApiModel\typespec\main.tsp`).
Retain the reviewed contract revision with the acceptance record; newer API
versions are not silently substituted.

The diagnostic snapshot is a **root object**, not ARM `properties`: the
verifier compares `entityName`, `healthState` and the exact complete
`signalInputs` set using each member's `name` and `effectiveHealthState`.
Separately, ARM must return the same entity ID/name and expected
`properties.healthState`, with the complete external set at
`properties.signalGroups.external.signals`. Each external signal must have the
expected `name` and `status.healthState`. An optional numeric expected `value`
requires exact numeric equality to ARM `status.value`; omit it when a scenario
does not require a deterministic value.

For nonempty expected sets, **every ARM `status.reportedAt`** must be present,
valid, no older than `-NotBefore` and not more than five seconds in the future.
Although optional in the ARM contract, an absent report timestamp cannot
establish freshness and fails this gate. Diagnostic transition timestamps
(`lastHealthTransitionAt`) are **not** report times and are never substituted.
Both views must match in the same polling pass within one common deadline.
Failed data-plane reads never fall back to ARM.

**Success separates state, observed ARM report freshness, and image/identity/
network metadata from TTL. Exact TTL/expiration is not verified.** The diagnostic
members contain no report or expiry timestamps; ARM provides an observed
`reportedAt`, not an `expiresAt`. Neither snapshot directly proves configured
TTL or exact expiration. Never manufacture service evidence by adding configured
TTL to `reportedAt`. Use the separately authorized behavioral expiration
sequence below.

Create a non-secret expected-state JSON file, substituting actual existing
resource IDs and the exact registry-qualified manifest reference. This example
is the checkout demo's baseline after reports have materialized:

```json
{
  "containerGroupId": "<ACI resource ID>",
  "identityResourceId": "<attached UAMI resource ID>",
  "registryResourceId": "<ACR resource ID>",
  "vaultResourceId": "<Key Vault resource ID>",
  "imageReference": "<registry>.azurecr.io/evaluator@sha256:<64 lowercase hex>",
  "networkMode": "public",
  "models": [{
    "resourceId": "/subscriptions/<subscription-guid>/resourceGroups/<model-group>/providers/Microsoft.CloudHealth/healthmodels/<actual-model-name>",
    "dataPlaneUri": "https://<existing model's actual data-plane hostname>",
    "apiVersion": "2026-10-01-preview",
    "controlPlaneApiVersion": "2026-10-01-preview",
    "entities": [
      {"name":"checkout-api","healthState":"Healthy","signals":[
        {"name":"p95-latency","healthState":"Healthy"},
        {"name":"checkout-error-rate","healthState":"Healthy"},
        {"name":"availability","healthState":"Healthy","value":1}
      ]},
      {"name":"checkout-traffic","healthState":"Healthy","signals":[
        {"name":"traffic-freshness","healthState":"Healthy"}
      ]},
      {"name":"checkout","healthState":"Healthy","signals":[]}
    ]
  }]
}
```

For `vnetNat` / `private`, also provide `subnetId` and `natGatewayId`.
For a vault-only public-access override, add `"keyVaultPublicNetworkAccess":
"Disabled"` to the expected-state JSON. This optional field describes the
vault's effective public access independently of the registry; omission uses
the existing mode default (`Enabled` for `public` / `vnetNat`, `Disabled` for
`private`). Invalid values and an expected `Enabled` vault in `private` mode
are rejected before credential acquisition. Private mode still requires
approved private endpoints for both resources.
This readback mode requires each model ID's subscription to match the expected
ACI subscription; it does not select subscriptions or infer cross-subscription
authorization. Use dedicated existing acceptance entities with only the
intended inputs: comparison is the **exact complete set**, including no extra,
missing or duplicate names. It does not hide other producers' signals with a
name filter.

An intentionally empty expected signal array supports the grouping entity
`checkout` and post-expiration absence checks. ARM may omit optional
`signalGroups`, `external` or `signals` for that absence; present properties/
groups must be objects and a present signals field must be an array. Nulls,
scalars and other malformed present shapes fail closed. Diagnostic `signalInputs`
must still be an array, and entity health must match in **both** views.
Empty entities establish state/absence, **not report freshness**, even when
other entities in the invocation have fresh reports.

Acquire Azure CLI credentials separately through an authorized login; the
verifier never logs in, selects another account or infers the intended identity.
The caller needs read access to ACI, registry, vault, subnet (if selected), the
model data-plane diagnostics **and ARM entity status**. Specify the caller's **Entra object/principal
UUID** and tenant UUID, not its client/application ID. These can be different
from the evaluator UAMI. The script acquires ARM and data-plane tokens and
checks their `oid`, `tid`, audience and remaining validity before use. It sends
tokens only as HTTP headers, never shell arguments or output; this local
identity check is not a replacement for service-side signature validation.
The data-plane scope remains `https://data.cloudhealth.azure.com/.default`.
Entra v2 tokens can identify that resource with CloudHealth's application
audience `36e21f15-cda0-45c2-9932-98f28a8bdb28` rather than its resource URI.
The verifier accepts that documented application ID only for the data-plane
credential; ARM still requires its own resource audience. Unrelated application
IDs, unexpected principals/tenants and insufficient token lifetimes fail before
any HTTP request.

```powershell
# Authorized operator usage only; not executed by offline validation.
# Save this timestamp BEFORE the deployment/restart being verified:
# $rolloutStarted = [DateTimeOffset]::UtcNow
# ... independently authorized deployment/restart ...
# & .\scripts\verify-e2e.ps1 -AuthorizeReadback `
#     -ExpectedFile artifacts\expected-readback.json `
#     -CloudHealthDiagnostics `
#     -ExpectedPrincipalId '<operator-object-id>' -ExpectedTenantId '<tenant-id>' `
#     -NotBefore $rolloutStarted -TimeoutSeconds 180 -PollSeconds 5
# if ($LASTEXITCODE -ne 0) { throw 'Acceptance failed; do not promote' }
```

Observed timestamps **must** carry `Z` or an explicit valid numeric offset;
offset-less values fail closed. UTC/local-kind dates materialized
by PowerShell's JSON parser are converted directly to instants, retaining
subsecond ticks rather than formatting/reparsing in the operator's time zone.
This uses the PowerShell 7.4-compatible API, not the 7.5-only `-DateKind` switch.
Mismatch polls until the global deadline; HTTP/auth/schema failures are explicit,
sanitized failures. Requests have no redirects and at most 20 seconds each,
bounded by the remaining deadline. Output contains counts and fixed phases, not
snapshots, signal context, token values or native authentication errors.

Metadata checks require one running Linux/Standard/Always worker, the exact
image, attached UAMI and identity-based registry credentials without passwords,
no evaluator public IP/inbound ports, and matching network-mode tag. VNet modes
also require the exact delegated subnet/NAT association; private mode requires
disabled public ACR/vault access and approved private endpoint connections.
This is **configuration evidence**, not proof of UAMI role propagation, actual
pull identity on a fresh allocation, private DNS resolution, absence of unrelated
public resources, or ACI-to-service connectivity. Verify those separately in the
authorized environment.

### Existing mapped snapshot mode

The separate **`-SnapshotMappingFile`** parameter set remains available and
unchanged in its required evidence. It cannot be combined with
`-CloudHealthDiagnostics`. Supply a reviewed mapping for an actual contract
that exposes all five fields, not pointers invented to fit CloudHealth's
diagnostics:

| Mapping key | RFC 6901 pointer target |
| --- | --- |
| `signals` | The complete external-signal array on the entity snapshot |
| `name` | Each signal's canonical name (relative to that signal) |
| `state` | Each signal's current health state |
| `reportedAt` | Its report timestamp |
| `expiresAt` | Its service-observed expiration timestamp |

Mapped mode retains the data-plane route at `2026-06-01-preview`. Its model
entry needs `dataPlaneUri` and `entities`; each expected signal requires
`name`, `healthState` and integer `expiresInMinutes` (1–10080). The existing
metadata envelope and identity/authorization parameters remain required.
It checks fresh `reportedAt`, positive remaining lifetime, and
`expiresAt - reportedAt` equal to the expected TTL within one second, with
strict explicit zones on both timestamps. Missing paths/timestamps fail closed.
There is no default mapping, synthetic expiration or ARM fallback.
The mapped snapshot mode is a generic adapter, **not** a service schema;
CloudHealth's reviewed diagnostics do not meet that timestamp contract.

### INT acceptance sequence and behavioral expiration

The following sequence is **unexecuted and requires separate INT authorization**,
including any deployment, role changes, demo faults, reporting interruption,
restart and cleanup. Offline checks (build, Bicep compile, script analysis and
unit tests) do not establish INT
connectivity, accepted reports or expiration:

1. Confirm the actual model ARM ID/root endpoint pairing and supported versions
   through trusted setup. Verify subscription/tenant/operator object ID,
   source-query and report authorization, independently deployed evaluator
   image/configuration, and endpoint reachability from ACI. A successful local
   token identity check does not prove service-side authentication/RBAC.
2. Record a rollout lower bound before the authorized evaluator rollout.
   Wait for demo ingestion and evaluation; require ready/live separately from
   baseline `-CloudHealthDiagnostics` readback of all three entities/four
   external signals above. Preserve the sanitized counts/phases, expected-file
   revision, image digest, configuration revision and protected service
   observations. `checkout` has no direct reports: no freshness claim applies
   to its empty signal set.
3. With explicit fault-control authorization, exercise demo `slow`, `very-slow`,
   `errors`, then `healthy` recovery. Account for ingestion, one-minute
   evaluation and five-minute rolling query windows. Correlate observed ARM
   values and workspace queries with strict latency **>500 / >1000 ms**, error
   rate **>5 / >10 percent**, availability **<1**, and traffic age **>30 / >60
   seconds**. Equality does not cross a threshold. Require expected entity/
   signal state transitions and fresh ARM reports after each lower bound.
   Use expected `value` for deterministic checks such as availability 1/0;
   retain protected observations for changing latency/rate values instead of
   asserting a fictitious fixed exact number. Missing/stale/nonfinite query
   data must follow configured **`noData: Unknown`**, not a fabricated healthy
   zero. Traffic interruption or scrape loss needs its own authorization.
4. For **behavioral TTL evidence**, first capture fresh accepted report
   observations and the configured interval/TTL (demo defaults: one minute,
   multiplier 3). Stop **only the separately owned evaluator's reporting**
   under explicit authorization, ensuring no second producer or in-flight
   report can refresh the same keys. Record stop/drain and observation times.
   After the configured TTL plus an agreed bounded propagation allowance,
   require the external reports to disappear and the expected Unknown/
   aggregation outcome in both views. Use an empty-signal expectation for
   absence/state, not a report-freshness claim. Record the last-present and
   first-absent observation window and service receipt/report-time limitations:
   this is observed expiration behavior, **not an exposed exact `expiresAt`**.
   A stop request by itself, or configured TTL arithmetic, is not proof.
5. Independently restart the evaluator only after authorization; capture a new
   `NotBefore`, then require fresh baseline reports and recovery in both views.
   A manual restart proves recovery, not liveness-triggered restart.
6. Clean up only resources owned by each lifecycle, through its authorized
   manifest-based teardown. Evaluator teardown must not delete demo resources
   or the existing model; demo teardown is separate. Retain evidence and
   manifests after failures rather than broadening permissions or deleting
   resource groups to obtain a pass.

## Acceptance scenarios, rotation and recovery

All rows below remain **unexecuted live**. Preserve each timestamp, expected
file, complete non-secret desired parameters, package validation evidence and
manifest digest in the release record. Apply the same sequence for each selected
network mode; do not let a public-mode pass stand in for private networking.

| Scenario | Authorized action and required evidence |
| --- | --- |
| Static baseline | Deploy a validated Static image/config to an existing test entity. Use a known value/threshold, interval `1m` and TTL `3`. Require ready/live plus exact dual-view state, observed ARM report freshness and metadata checks. Verify expiration behavior separately. |
| Configuration-only redeploy | Reuse the same manifest digest and complete replacement environment. Change interval, remove a signal and add a differently named signal. Confirm cadence from observed fresh reports and TTL behavior separately; wait the old TTL and convergence for removed signals to disappear before exact-set readback. No live environment mutation/hot reload. |
| Package-image update | Run package gate → new selected-package image → exact-image config gate → deployment. Verify a **new registry manifest digest**, same intended config and fresh reports. Never infer a successful pull from a local image ID. |
| Digest/config rollback | Redeploy the retained prior manifest **and complete prior configuration**, using the same gates. Verify prior signal set/state and observed report freshness, with separate TTL-behavior evidence, not old cached model state. |
| Liveness restart | In an explicitly authorized test environment, induce a real scheduler stall/process failure and observe ACI's liveness/Always restart and a new start event. Run probes and require new reports/readback. An operator `az container restart` proves manual recovery only, **not** liveness-triggered restart. No production stall switch is provided. |
| Unversioned credential rotation | Authorized secret tooling creates a new Key Vault version while the config reference omits `/version`. Wait cache TTL (default `5m`) plus auth refresh/poll. Verify sink auth recovery, fresh model reports and absence of credential logging. No image change is needed. |
| Pinned credential rotation | Change the `/version` reference, exact-image validate and redeploy the full environment. A cache expiration does not unpin a version. Retain the prior reference/config for authorized rollback. |

Changes to region/OS/SKU/CPU/memory/zones/subnet may require deletion/recreation.
`-ConfirmRecreate` permits consideration of deletion; it does not replace
`ShouldProcess` confirmation. Prefer a maintenance window. Both compatible
updates and recreation interrupt reporting; neither is a rolling revision.
Never delete the resource group to fix a single evaluator or loosen network/
secret policies to turn a failed check green.

When acceptance fails, keep the bounded/sanitized phase and correlate protected
deployment/ACI events, auth/rate-limit metrics, structured start logs and
service request IDs through approved channels. Readiness does not prove accepted
reports; a 202 does not prove recalculation; ingestion/telemetry delay can hide
a gap. Removed signals can remain active until TTL, expired reports and
forced-stop queues are not replayed, and there is no automatic configuration/
image rollback. Two workers writing the same key can overwrite each other;
operate exactly one intended worker.

Official schema/behavior references:

- [ACI 2025-09-01 template reference](https://learn.microsoft.com/azure/templates/microsoft.containerinstance/2025-09-01/containergroups)
- [Managed-identity ACR pull](https://learn.microsoft.com/azure/container-instances/using-azure-container-registry-mi)
- [ACI VNet requirements](https://learn.microsoft.com/azure/container-instances/container-instances-vnet)
- [ACI NAT Gateway](https://learn.microsoft.com/azure/container-instances/container-instances-nat-gateway)
- [Private ACR endpoints](https://learn.microsoft.com/azure/container-registry/container-registry-private-link)
- [ARM template limits](https://learn.microsoft.com/azure/azure-resource-manager/templates/best-practices#template-limits)
