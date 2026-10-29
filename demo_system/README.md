# Checkout demo

This demo creates a synthetic checkout API, a traffic generator, and Prometheus
in Azure. Prometheus sends metrics to an Azure Monitor workspace. A separately
deployed **Self-Hosted Evaluator** reads those metrics and reports health to the
demo's Azure Monitor Health Model.

Use it to see how latency and errors affect health, then watch the system
recover. There are no real payments, orders, or customer data. The workload has
no public IP or inbound ports.

**The demo does not deploy the evaluator.** The two deployments have separate
identities, configuration, and cleanup scripts.

## Requirements

- PowerShell **7.4+**, the exact .NET SDK pinned in
  [global.json](../global.json), and standalone Bicep.
- A working **Linux Docker daemon with buildx**.
- Azure CLI signed in to an authorized **AzureCloud** subscription.
- A region and subscription that support the health-model preview API
  `2026-10-01-preview`, with permission to deploy resources, push images, and
  assign roles.

Check the [full prerequisites](docs/deployment.md#prerequisites), including
resource providers and permissions, before deploying. **The demo incurs ongoing
container, registry, and monitoring charges until you remove it.**

Run all commands below from the repository root.

## 1. Deploy the workload

Set your subscription and deployment location, then preview the command:

```powershell
$deployment = @{
    SubscriptionId = '<your-authorized-subscription-guid>'
    ResourceGroupName = 'checkout-demo-rg'
    Location = 'eastus'
}
& .\demo_system\scripts\deploy.ps1 @deployment -WhatIf
```

`-WhatIf` checks local inputs only; it does not contact Azure or confirm capacity
or permissions. After reviewing the requirements and authorizing the Azure
changes, deploy:

```powershell
& .\demo_system\scripts\deploy.ps1 @deployment
```

The script creates the resources, builds and pushes the image, and waits for
the containers to be ready. Traffic starts automatically, with a five-second
wait between completed requests by default.

Keep **`demo_system\artifacts\deployment.json`**. This manifest records the
resources used by later commands and cleanup, including after a deployment
failure. See [deployment options and recovery](docs/deployment.md) for traffic
settings, alternate NuGet feeds, tool paths, and failed rollouts.

## 2. Connect an evaluator

Build an evaluator image containing the `azure-monitor` package using the
[package and image instructions](../docs/image-delivery.md#azure-monitor-package).
Obtain the demo model's **verified root HTTPS reporting URL** from the model's
`properties.dataPlaneUri` (ARM api-version `2026-12-01-preview`; see
[Connect the evaluator](docs/evaluator-setup.md#1-get-the-model-reporting-url))
or from your service owner. The demo cannot discover this URL; the
model resource ID and workspace query endpoint are not substitutes.

Generate configuration locally:

```powershell
$healthModelDataPlaneUri = '<verified-health-model-root-HTTPS-URL>'
& .\demo_system\scripts\new-evaluator-configuration.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json `
    -HealthModelDataPlaneUri $healthModelDataPlaneUri `
    -OutputDirectory .\demo_system\artifacts\evaluator
```

The evaluator needs **its own managed identity**, with Monitoring Data Reader
on the demo workspace and Contributor scoped to the demo health model.
The demo does not grant these permissions.

Follow [Connect the evaluator](docs/evaluator-setup.md) to apply the grants,
validate the generated configuration, merge the parameter fragment into your
complete evaluator inputs, and deploy. The generated fragment is **not** a
complete deployment file.

## 3. Try faults and recovery

With the evaluator reporting, change the checkout behavior:

```powershell
$manifest = '.\demo_system\artifacts\deployment.json'
& .\demo_system\scripts\set-fault.ps1 -ManifestPath $manifest -Mode errors
```

After observing the fault in the model, restore normal responses:

```powershell
& .\demo_system\scripts\set-fault.ps1 -ManifestPath $manifest -Mode healthy
```

| Mode | Checkout response |
| --- | --- |
| `healthy` | 200 after about 50 ms |
| `slow` | 200 after about 750 ms |
| `very-slow` | 200 after about 1500 ms |
| `errors` | 500 for every valid checkout |

Modes stay active until changed or the API restarts. Allow time between changes:
signals are evaluated every minute, and latency/error queries use five-minute
windows. Initial permission propagation and metric ingestion can also delay
results.

Check the model's latency, error-rate, availability, and traffic-freshness
signals. **Container readiness does not prove end-to-end health.** See
[signals and expected behavior](docs/reference.md#signals-and-expected-behavior)
and the [verification steps](docs/evaluator-setup.md#5-verify-the-results).

## 4. Remove the demo

Preview cleanup, then run it after authorizing deletion:

```powershell
& .\demo_system\scripts\remove.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json -WhatIf

# Deletes the demo resources after confirmation.
& .\demo_system\scripts\remove.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json
```

Cleanup deletes the demo model, registry images, workspace metrics, and other
owned demo resources. It retains the resource group, unrelated resources, and
local manifest. **It does not remove the evaluator.** Use the evaluator's
[separate teardown](../docs/deployment.md#teardown) when it is no longer needed.
Never manually delete Azure's workspace-managed ingestion group.
See [cleanup and recovery details](docs/deployment.md#teardown).

## Details and examples

| What you need | Where to go |
| --- | --- |
| Deployment requirements, options, and cleanup | [Demo deployment guide](docs/deployment.md) |
| Evaluator configuration, permissions, and verification | [Evaluator setup guide](docs/evaluator-setup.md) |
| Signal thresholds, timing, and local development checks | [Demo reference](docs/reference.md) |
| Exact queries and Prometheus settings | [signals.json](config/signals.json) and [prometheus.yml](infra/prometheus.yml) |
| Sample scripts, application code, and Azure templates | [Scripts](scripts), [application](src/Checkout.Demo), and [infrastructure](infra) |
