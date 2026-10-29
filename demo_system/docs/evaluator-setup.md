# Connect the evaluator

The [checkout demo](../README.md) produces metrics and creates the health model.
The evaluator reads those metrics and sends health reports. You must build,
authorize, and deploy the evaluator separately; configuration generation does
none of those steps.

Run the commands below from the repository root.

## 1. Get the model reporting URL

Read the **health-model data-plane root HTTPS URL** from the model's ARM
resource. Confirm the model resource ID matches `healthModelResourceId` in the
demo manifest.

The read-only `properties.dataPlaneUri` is returned only by api-version
`2026-12-01-preview`; earlier versions do not expose it:

```powershell
az rest --method get --url "https://management.azure.com<healthModelResourceId>?api-version=2026-12-01-preview" --query properties.dataPlaneUri -o tsv
```

Neither the demo manifest nor the generator discovers it, and the evaluator's
data-plane `apiVersion` is an unrelated setting.

| Value | Purpose |
| --- | --- |
| Manifest `healthModelResourceId` | ARM resource identity and model-scoped role assignments; not a URL |
| Manifest `workspaceQueryEndpoint` | Azure Monitor Prometheus queries |
| Your `HealthModelDataPlaneUri` | Health-model reporting; not the workspace or ARM endpoint |

The generator requires root HTTPS URLs without credentials, extra paths,
query, or fragment.

## 2. Build an evaluator image

Follow the [Azure Monitor package example](../../docs/image-delivery.md#azure-monitor-package),
then the [image build instructions](../../docs/image-delivery.md#build-locally-push-only-explicitly).
Include the **`azure-monitor` package**; generated configuration selects version
`1.*`. Keep any other packages required by your existing evaluator configuration.

Both metric queries and health reports use the **evaluator's own managed
identity**, not the demo identity. The demo identity receives only AcrPull on
the demo registry and Monitoring Metrics Publisher on the custom DCR.

## 3. Generate configuration

```powershell
$healthModelDataPlaneUri = '<verified-health-model-root-HTTPS-URL>'
& .\demo_system\scripts\new-evaluator-configuration.ps1 `
    -ManifestPath .\demo_system\artifacts\deployment.json `
    -HealthModelDataPlaneUri $healthModelDataPlaneUri `
    -OutputDirectory .\demo_system\artifacts\evaluator
```

Generation is local-only. It writes these files:

| File | How to use it |
| --- | --- |
| `configuration.json` | Input to `validate-config.ps1 -ConfigurationFile`; contains `plugins`, `models`, and `hostSettings` |
| `environment.json` | JSON mapping `SHE_PLUGIN_AZURE_MONITOR` and `SHE_MODEL_CHECKOUT` to JSON strings; not a shell script or `.env` file |
| `evaluator.parameters.fragment.json` | ARM parameter fragment containing only `pluginConfigurations`, `modelConfigurations`, and `healthModelResourceIds` |

The plugin configuration is named `azure-monitor-demo`, with key `AZURE_MONITOR`.
The model configuration is named `checkout-demo`, with key `CHECKOUT`.
Signals evaluate every minute with `ttlMultiplier: 3`. No demo client ID or
credential is exported. Exact queries and thresholds come from
[signals.json](../config/signals.json).

Validate against your independently built image. Set `$imageReference` to the
immutable image reference returned by your image build:

```powershell
& .\scripts\validate-config.ps1 `
    -ImageReference $imageReference `
    -ConfigurationFile .\demo_system\artifacts\evaluator\configuration.json
if ($LASTEXITCODE -ne 0) { throw 'Configuration rejected; do not deploy' }
```

This validation is offline; it does not prove that the endpoints or permissions
work.

## 4. Grant access and deploy the evaluator

Two separate grants are required. **The demo grants neither to the evaluator.**

### Workspace query access

An authorized role-assignment operator must grant **Monitoring Data Reader** on
the demo workspace to the evaluator's managed identity. After approval:

```powershell
$demo = Get-Content .\demo_system\artifacts\deployment.json -Raw | ConvertFrom-Json
$evaluatorPrincipalId = '<your evaluator managed identity object/principal GUID>'
az role assignment create `
    --subscription $demo.subscriptionId `
    --assignee-object-id $evaluatorPrincipalId `
    --assignee-principal-type ServicePrincipal `
    --role 'Monitoring Data Reader' `
    --scope $demo.workspaceResourceId `
    --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Workspace role assignment failed' }
```

The query token scope is
`https://prometheus.monitor.azure.com/.default`.

### Model reporting access

Include the demo model ID in the evaluator's complete `healthModelResourceIds`
inputs. The [evaluator deployment](../../docs/deployment.md#permissions-and-secret-bootstrap)
assigns built-in **Contributor scoped only to that health model**. It creates
role assignments, not custom roles or subscription-wide reporter access.

Required actions are `Microsoft.CloudHealth/healthmodels/read`,
`Microsoft.CloudHealth/healthmodels/entities/read`, and
`Microsoft.CloudHealth/healthmodels/entities/ingestHealthReport/action`.
These are regular actions, not data actions. Contributor also permits
modifying or deleting this model and its children; that broader model-scoped
access is intentional.

Merge the generated parameter fragment into your **complete desired evaluator
inputs**, preserving other required parameters, plugins, models, and model
access. It is not a deployable complete file or a partial-update request.
Validate the combined configuration and follow the
[evaluator deployment steps](../../docs/deployment.md#delivery-and-safe-deployment-phases).
If a new evaluator identity is needed, follow the deployment's staged workflow
and apply the workspace grant once that identity exists.

## 5. Verify the results

Check metric ingestion, workspace query results, evaluator reports, and model
health separately. `DEMO_READY` and local tests do not establish end-to-end
acceptance.

Use [authorized readback](../../docs/deployment.md#authorized-readback) and the
[integration acceptance sequence](../../docs/deployment.md#int-acceptance-sequence-and-behavioral-expiration).
Supply the actual model `resourceId` and `dataPlaneUri`, the declared data-plane
`apiVersion` (`2026-06-01-preview` or `2026-10-01-preview`), and
`controlPlaneApiVersion: 2026-10-01-preview` with `-CloudHealthDiagnostics`.

Both diagnostic state and ARM external status must match. Entities with signals
need observed ARM `status.reportedAt` to establish freshness. The grouping entity
`checkout` has no signals: its state can be checked, but report freshness cannot
be claimed for it.

These views do not expose exact expiration or TTL evidence. Use the separately
authorized stop/wait/recovery observations in the acceptance sequence to check
expiration behavior. See the [demo reference](reference.md#signals-and-expected-behavior)
for expected timing and fault behavior.

The demo and evaluator keep separate manifests and lifecycles. Removing either
one does not clean up the other.
