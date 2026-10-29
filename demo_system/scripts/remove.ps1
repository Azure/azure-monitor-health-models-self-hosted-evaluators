#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([ValidateNotNullOrEmpty()][string] $ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\deployment.json'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$phase = 'manifest validation'
try {
    $manifest = Read-DemoManifest $ManifestPath
    $targets = @(Get-DemoTarget $manifest)
    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess(($targets.id -join "`n"), 'Delete ONLY these demo resources, retaining the resource group and manifest')
        return
    }
    $phase = 'public cloud and subscription validation'
    Assert-DemoCloud $manifest.subscriptionId
    $phase = 'ownership preflight'
    $live = @(Get-DemoLiveResource $manifest.subscriptionId $manifest.resourceGroupName)
    Assert-DemoOwnership $targets $live $manifest.owner
    $present = @($targets | Where-Object { $_.id -iin $live.id })
    foreach ($target in $targets) {
        if ($target.id -inotIn $present.id) { Write-Output "Demo target already absent (or selected group already absent): $($target.id)" }
    }
    if ($present.Count -eq 0) { return }
    if (-not $PSCmdlet.ShouldProcess(($present.id -join "`n"), 'Delete ONLY these demo resources in dependency order; retain group, unrelated resources and manifest')) { return }
    $phase = 'resource deletion'
    foreach ($target in $present) {
        # Parent deletion removes model children and resource-scoped assignments. Azure owns workspace-managed ingestion cleanup.
        $null = Invoke-DemoAz @('resource', 'delete', '--ids', $target.id, '--api-version', $target.apiVersion) $manifest.subscriptionId "delete $($target.type)" -TimeoutSeconds 600
        Write-Output "Deleted: $($target.id)"
    }
}
catch {
    [Console]::Error.WriteLine("Demo teardown failed ($phase): $($_.Exception.Message) Check permissions/state and rerun with the retained manifest. Never delete the resource group or Azure-managed ingestion group.")
    exit 1
}
