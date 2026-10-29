# Self-Hosted Evaluators

Self-Hosted Evaluators connects external monitoring systems to **Azure Monitor
Health Models**. It runs as a .NET worker in your Azure environment, reads
monitoring data on a schedule, and reports `Healthy`, `Degraded`, `Unhealthy`,
or `Unknown` for the signals you configure.

Included integrations support **Datadog**, **Splunk Observability**, and
**Azure Monitor workspaces (managed Prometheus)**. A Static integration supplies
fixed values for testing. You can also add your own integrations.

The evaluator uses existing health models and entities; it does not create them
or store your source telemetry. Azure Monitor Health Models combines the
reported signals to determine overall health.

To try a sample workload, start with the [checkout demo](demo_system/README.md).
The demo creates its own workload and health model, but you deploy the evaluator
separately.

## Requirements

- An Azure subscription with access to Azure Monitor Health Models, plus an
  existing model and entities to receive reports.
- Access to your monitoring source, its query endpoint, and the credentials or
  identity permissions needed to read it.
- PowerShell **7.4+**, the exact .NET SDK pinned in [global.json](global.json),
  and a working **Linux Docker daemon with buildx**.
- Azure CLI signed in to the intended **AzureCloud** subscription, standalone
  Bicep, and permission to create resources and assign roles.

Review the [deployment prerequisites and inputs](docs/deployment.md#tooling-and-inputs)
and [permissions](docs/deployment.md#permissions-and-secret-bootstrap) before
making Azure changes. Deployment creates billable resources.

## Set up an evaluator

Run commands from the repository root.

1. **Collect your endpoints and model details.** Get the monitoring source's
   query endpoint, the health model's resource ID and reporting URL, and the
   entity names you want to monitor. Use the reporting URL supplied by your
   model setup; do not derive it from the resource ID.
2. **Configure sources and signals.** Start with
   [infra/main.bicepparam](infra/main.bicepparam), replacing the sample model,
   plugin, and signal values. Each signal names a query and rules for converting
   its result into a health state. See the
   [configuration examples and options](docs/configuration.md) and
   [authentication guide](docs/auth-runtime.md). Keep credentials in Key Vault;
   configuration contains references, not secret values.
3. **Publish and validate your integrations.** Include only the plugin packages
   you need. Follow the [package validation example](docs/image-delivery.md#validate-a-user-built-package)
   or the [Azure Monitor package example](docs/image-delivery.md#azure-monitor-package).
   Packages are bundled into the image, not downloaded at runtime.
4. **Build and check the image.** Use
   [build-image.ps1](scripts/build-image.ps1), then
   [validate-config.ps1](scripts/validate-config.ps1) with your complete
   configuration. The [image delivery guide](docs/image-delivery.md) provides
   commands and sample inputs. These checks do not contact your monitoring
   source or health model.
5. **Deploy to Azure.** Follow the [deployment steps](docs/deployment.md#delivery-and-safe-deployment-phases)
   using [deploy.ps1](scripts/deploy.ps1). The script creates the supporting
   resources, builds and pushes an image or reuses an existing image digest,
   validates it, and starts the worker in Azure Container Instances (ACI).
   Keep the deployment's ownership manifest, including after a failed rollout.
6. **Check reports in the model.** Follow the
   [end-to-end verification instructions](docs/deployment.md#authorized-readback).
   A ready container alone does not confirm that queries or health reports
   are succeeding.

Configuration is read at startup. To change it, redeploy with the **complete
desired configuration**; to change plugin code, build a new image. See
[updates and rollback](docs/deployment.md#runtime-limits-and-rollback) and
[safe teardown](docs/deployment.md#teardown) for lifecycle instructions.

## Develop locally

Install the [local validation tools](docs/image-delivery.md#local-validation-prerequisites),
including Bicep and PSScriptAnalyzer, then restore and run the checks:

```powershell
dotnet restore SelfHostedEvaluators.slnx
if ($LASTEXITCODE -ne 0) { throw 'Restore failed' }
& .\scripts\test.ps1
if ($LASTEXITCODE -ne 0) { throw 'Validation failed' }
```

These checks do not require Azure access. For a new data source, follow the
[plugin authoring guide](docs/plugin-authoring.md), using the
[Static plugin](src/Plugins.Static) as a small implementation example and the
[shared test helpers](src/Plugins.Testing) for plugin tests.

## Guides and examples

| What you need | Where to go |
| --- | --- |
| A sample workload with faults and recovery | [Checkout demo](demo_system/README.md) |
| Configuration fields, thresholds, intervals, and credentials | [Configuration reference](docs/configuration.md) |
| Source-specific query examples | [Azure Monitor](docs/configuration.md#azure-monitor-workspace-managed-prometheus), [Datadog](docs/plugin-authoring.md#datadog-settings-and-queries), [Splunk Observability](docs/plugin-authoring.md#splunk-observability-settings-and-queries) |
| Package, image, and configuration commands | [Image delivery](docs/image-delivery.md) and [scripts](scripts) |
| Azure permissions, networking, deployment, and cleanup | [Deployment guide](docs/deployment.md) and [sample parameters](infra/main.bicepparam) |
| Probes, logs, and worker commands | [Host runtime](docs/host-runtime.md) |
| Authentication, polling, and report delivery details | [Authentication](docs/auth-runtime.md), [polling](docs/polling-runtime.md), [reporting](docs/reporting-runtime.md) |
| Custom integrations and interfaces | [Plugin authoring](docs/plugin-authoring.md) and [plugin contract](docs/plugin-contract.md) |
| Architecture and design | [System diagram](system-diagram.md) and [design specification](design-spec.md) |
