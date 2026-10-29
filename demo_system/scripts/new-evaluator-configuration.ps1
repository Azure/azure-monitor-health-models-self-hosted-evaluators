#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateNotNullOrEmpty()][string] $ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\deployment.json'),
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $HealthModelDataPlaneUri,
    [ValidateNotNullOrEmpty()][string] $OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\evaluator')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
try {
    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { throw 'OutputDirectory must not be empty.' }
    $manifest = Read-DemoManifest $ManifestPath
    Assert-DemoRootEndpoint $HealthModelDataPlaneUri
    $signals = ConvertFrom-EvaluatorConfigurationJson (Get-Content -LiteralPath (Join-Path $PSScriptRoot '..\config\signals.json') -Raw)
    if ($signals -isnot [array] -or $signals.Count -ne 4) { throw 'The authoritative four-signal file is required.' }
    $plugin = @{
        schemaVersion = 1; name = 'azure-monitor-demo'; package = 'azure-monitor'; packageVersion = '1.*'
        endpoints = @{ default = $manifest.workspaceQueryEndpoint }
        auth = @{ type = 'entraId'; azure = @{ type = 'managedIdentity' }; scope = 'https://prometheus.monitor.azure.com/.default' }
    }
    $model = @{
        schemaVersion = 1; name = 'checkout-demo'
        healthModel = @{ dataPlaneUri = $HealthModelDataPlaneUri; apiVersion = '2026-10-01-preview'; auth = @{ type = 'managedIdentity' } }
        defaults = @{ interval = '1m'; ttlMultiplier = 3 }
        signals = $signals
    }
    $configuration = @{ plugins = @(@{ key = 'AZURE_MONITOR'; config = $plugin }); models = @(@{ key = 'CHECKOUT'; config = $model }); hostSettings = @{} }
    $environment = ConvertTo-EvaluatorEnvironment -Plugins $configuration.plugins -Models $configuration.models -HostSettings $configuration.hostSettings
    $fragment = @{
        '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'
        parameters = @{
            pluginConfigurations = @{ value = $configuration.plugins }
            modelConfigurations = @{ value = $configuration.models }
            healthModelResourceIds = @{ value = @($manifest.healthModelResourceId) }
        }
    }
    Write-DemoJson (Join-Path $OutputDirectory 'configuration.json') $configuration
    Write-DemoJson (Join-Path $OutputDirectory 'environment.json') $environment
    Write-DemoJson (Join-Path $OutputDirectory 'evaluator.parameters.fragment.json') $fragment
    Write-Output 'Configuration written. ARM parameters are a fragment only: independently configure/deploy the evaluator and its workspace-reader/model-reporter permissions using the evaluator identity.'
}
catch {
    [Console]::Error.WriteLine("Demo configuration failed (local validation/output): $($_.Exception.Message)")
    exit 1
}
