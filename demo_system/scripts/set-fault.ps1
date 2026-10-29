#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [ValidateNotNullOrEmpty()][string] $ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\deployment.json'),
    [Parameter(Mandatory)][ValidateSet('healthy', 'slow', 'very-slow', 'errors', IgnoreCase = $false)][string] $Mode
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$phase = 'manifest validation'
try {
    $manifest = Read-DemoManifest $ManifestPath
    if (-not $PSCmdlet.ShouldProcess($manifest.containerGroupResourceId, "Set checkout fault to $Mode")) { return }
    $phase = 'internal fault command'
    $output = Invoke-DemoAz @('container', 'exec', '--resource-group', $manifest.resourceGroupName,
        '--name', $manifest.containerGroupName, '--container-name', 'checkout-api',
        '--exec-command', "/app/control/fault-$Mode.sh") $manifest.subscriptionId $phase -TimeoutSeconds 60
    Assert-DemoMarker $output "DEMO_FAULT:$Mode"
    Write-Output "DEMO_FAULT:$Mode"
}
catch {
    [Console]::Error.WriteLine("Demo fault control failed ($phase): $($_.Exception.Message) Check az login, ACI state and container logs; no public control endpoint is used.")
    exit 1
}
