# Deploy and manage the checkout demo

Use the [demo README](../README.md) for the basic deployment steps. This guide
covers requirements, optional settings, recovery, and cleanup. The demo scripts
never invoke the evaluator deployment.

## Prerequisites

- PowerShell **7.4+** and the exact .NET SDK in
  [global.json](../../global.json).
- A running **Linux Docker daemon with buildx**. Having only the CLI is not
  sufficient.
- Azure CLI signed in and authorized for the explicitly selected, enabled
  **AzureCloud** subscription. The scripts do not switch accounts or support
  sovereign clouds.
- Standalone **Bicep**, found on PATH, through `BICEP_CLI_PATH`, or with
  `-BicepPath`. Local tests use **0.48.1**, including its offline `snapshot`
  command.
- Registered providers: `Microsoft.ContainerInstance`,
  `Microsoft.ContainerRegistry`, `Microsoft.ManagedIdentity`,
  `Microsoft.Monitor`, `Microsoft.Insights`, and `Microsoft.CloudHealth`.
  Arrange registration through your authorized process.
- A region and subscription that support the health-model API
  `2026-10-01-preview`, with available ACI capacity and acceptable policies and
  quotas.

The operator needs permission to create the selected resource group if absent,
run group deployments, create the declared resources and model children, and
assign roles on the registry and data collection rule. The operator also needs
**ACR push** rights; AcrPull for the demo identity does not authorize an operator
to push images. Fault control requires ACI exec permission. Teardown requires
deletion rights on the owned resources.

## Resources and networking

One Linux Azure Container Instances (ACI) group runs `checkout-api`,
`traffic-generator`, and Prometheus 3.15.1, using **1 vCPU / 1.5 GB** in total.
Prometheus scrapes every 15 seconds and sends metrics through a dedicated data
collection endpoint (DCE) and data collection rule (DCR) to an Azure Monitor
workspace.

The deployment also creates a Basic Azure Container Registry (ACR), a managed
identity, and a health model containing `checkout`, `checkout-api`, and
`checkout-traffic`. See the [foundation template](../infra/main.bicep) and
[container template](../infra/runtime.bicep) for the complete resource settings.

Traffic, scrapes, and control requests use `http://127.0.0.1:8080` inside the
container group. ACI has **no public IP or inbound ports**. Supporting registry,
workspace, and ingestion services use authenticated public endpoints; this is
not a private-endpoint deployment.

All declared resources are in the selected resource group. Azure Monitor also
creates its default ingestion group, `MA_<workspace>_<region>_managed`.
**Never manually delete that managed group.** The custom DCE and DCR remain
in your selected group.

## Deployment options

Run [deploy.ps1](../scripts/deploy.ps1) from the repository root. Start with the
[README example](../README.md#1-deploy-the-workload).

| Parameter | Purpose / default |
| --- | --- |
| `-SubscriptionId` | Required authorized subscription GUID |
| `-ResourceGroupName` | Required group name; created if absent |
| `-Location` | Required Azure region |
| `-NamePrefix` | `shedemo`; 3-10 lowercase letters or digits |
| `-IntervalSeconds` | `5`; accepts 1-3600 seconds between completed traffic attempts |
| `-ManifestPath` | `demo_system\artifacts\deployment.json` |
| `-BicepPath` | Explicit executable path; otherwise `BICEP_CLI_PATH`, then PATH |
| `-NuGetSource` | Optional HTTPS restore feed; otherwise NuGet.org |
| `-WhatIf` | Local input preview only; no Azure, Docker, Bicep execution, network requests, or artifact writes |

A real run creates billable resources, builds and pushes a Linux image, pins its
registry manifest digest, and waits for all three containers to be Running with no restarts. It does not configure or deploy an evaluator.

### Alternate NuGet feed

Demo image restore defaults to `https://api.nuget.org/v3/index.json`. In a
restricted environment, select a reachable public feed explicitly:

```powershell
& .\demo_system\scripts\deploy.ps1 `
    -SubscriptionId '<your-authorized-subscription-guid>' `
    -ResourceGroupName 'checkout-demo-rg' `
    -Location 'eastus' `
    -NuGetSource 'https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-public/nuget/v3/index.json' `
    -WhatIf
```

The source must be an absolute HTTPS URI without credentials, query, or
fragment; feed paths are allowed. This is checked even under `-WhatIf`.
The override is passed to Docker as `NUGET_SOURCE`, not saved in global NuGet
configuration. There is no automatic fallback, and TLS certificate validation
stays enabled. Do not put secrets in build arguments.

## Deployment failures and recovery

Keep the manifest at `demo_system\artifacts\deployment.json`, or your explicit
`-ManifestPath`. It records the subscription, group, ownership, seven exact
resource targets, endpoints, and lifecycle status.

The manifest is first written after prerequisites succeed, **before image
build**, then updated with the image, readiness, or failure phase. It can be
used for cleanup after later build or runtime failures.

If prerequisites fail before a manifest is written, some resources may remain.
There is **no automatic rollback**. Inspect the selected group, resolve the
cause, and rerun with the same subscription, group, prefix, and location.
Preserve any earlier manifest.

Ownership conflicts stop deployment. An incompatible ACI update requires a
separately authorized teardown and redeployment; the script does not
automatically delete and recreate compute.

## Fault control

Use [set-fault.ps1](../scripts/set-fault.ps1) with the retained manifest:

```powershell
& .\demo_system\scripts\set-fault.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json -Mode slow

# Local preview only; no Azure call.
& .\demo_system\scripts\set-fault.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json -Mode errors -WhatIf
```

Modes are case-sensitive: `healthy`, `slow`, `very-slow`, and `errors`. The API
starts healthy after every restart; modes do not cycle automatically.
See [expected responses and health behavior](reference.md#signals-and-expected-behavior).

The script uses private ACI exec in `checkout-api` and requires the exact output
line `DEMO_FAULT:<mode>`. The control wrapper lives under
`/app/control`, with no ACI exec arguments. The wrappers are packaged in the
non-chiseled `10.0-noble` image, running as nonroot UID 1654. The application's
native CLI also supports `dotnet Checkout.Demo.dll fault errors`. No public control endpoint is exposed.

## Teardown

Follow the [README cleanup commands](../README.md#4-remove-the-demo) using
[remove.ps1](../scripts/remove.ps1). `-WhatIf` reads and validates the local
manifest; it does **not** check live ownership.

Real teardown checks every known target and its live ownership tag before
deleting anything. Any mismatch stops the operation. After confirmation,
deletion proceeds in this order: ACI, health model, custom DCR, custom DCE,
workspace, ACR, and demo identity. Stopping ACI first stops traffic.

The selected resource group, unrelated resources, local manifest, and evaluator
resources are retained. **Registry image data and workspace metric data are
lost.** Azure removes the managed ingestion group with the workspace; never
delete it manually.

Deletion is not transactional. Avoid concurrent ownership or tag changes.
Already-absent targets are reported separately from authentication or CLI
failures. After a partial failure, resolve the cause and rerun using the
retained manifest.
