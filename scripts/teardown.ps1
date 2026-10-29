#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param([Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ManifestPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'deployment-ownership.ps1')
$phase = 'ownership manifest validation'
try {
    $manifest = Read-EvaluatorManifest $ManifestPath
    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess(($manifest.resources.id -join "`n"),
            'Stop and delete manifest-owned evaluator resources; delete a rollout-created group only if no foreign resources exist')
        return
    }
    $phase = 'public cloud and subscription validation'
    $account = Invoke-EvaluatorAzJson @('account', 'show') $manifest.subscriptionId $phase
    if ($account.environmentName -cne 'AzureCloud' -or $account.id -ine $manifest.subscriptionId -or $account.state -cne 'Enabled') {
        throw 'An enabled Azure public-cloud subscription is required.'
    }
    $phase = 'resource inventory and ownership preflight'
    $inventory = @(Get-EvaluatorGroupInventory $manifest)
    $live = @(Get-EvaluatorOwnedLiveResource $manifest $inventory)
    Assert-EvaluatorLiveOwnership $manifest $live
    $owned = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $manifest.resources) { $null = $owned.Add($resource.id) }
    foreach ($id in @(Get-EvaluatorManagedResourceId $manifest $live)) { $null = $owned.Add($id) }
    $foreign = @($inventory | Where-Object { -not $owned.Contains($_.id) })
    foreach ($resource in $live) {
        if (@($foreign | Where-Object { $_.id.StartsWith("$($resource.id)/", [StringComparison]::OrdinalIgnoreCase) }).Count) {
            $phase = 'foreign child resources would be deleted with an owned parent; separate the shared resources before rerunning'
            throw 'Foreign child resource prevents safe cascade deletion.'
        }
    }
    $group = "/subscriptions/$($manifest.subscriptionId)/resourceGroups/$($manifest.resourceGroupName)"
    $groupOwned = $false
    $groupExists = Invoke-EvaluatorAzJson @('group', 'exists', '--name', $manifest.resourceGroupName) $manifest.subscriptionId 'resource group existence'
    if ($groupExists -isnot [bool]) { throw 'Invalid resource group existence response.' }
    if ($manifest.resourceGroupCreated -and $groupExists) {
        $groupState = Get-EvaluatorArmResource $group '2024-03-01'
        $groupOwned = $groupState.tags -is [Collections.IDictionary] -and $groupState.tags['she-owner'] -ceq $manifest.owner
    }
    $phase = 'local rollout container ownership preflight'
    $localContainers = @(Get-EvaluatorLocalContainer $manifest)
    if (-not $PSCmdlet.ShouldProcess(($manifest.resources.id -join "`n"),
            'STOP evaluator containers and permanently delete owned infrastructure, registry images, secrets and monitoring data; retain foreign resources')) { return }
    $phase = 'local rollout container shutdown'
    foreach ($container in $localContainers) {
        if ($container.State.Running) {
            $null = Invoke-EvaluatorCommand docker @('stop', '--time', '60', $container.Id) $phase -TimeoutSeconds 120
        }
    }
    # A --rm validation container may already have disappeared after stopping.
    foreach ($container in @(Get-EvaluatorLocalContainer $manifest)) {
        if ($container.State.Running) { throw 'A rollout container is still running; stop concurrent rollout activity before retrying.' }
        $null = Invoke-EvaluatorCommand docker @('rm', $container.Id) 'local rollout container removal' -TimeoutSeconds 60
    }
    $priority = @{
        'Microsoft.ContainerInstance/containerGroups' = 0
        'Microsoft.Authorization/roleAssignments' = 2
        'Microsoft.Network/privateEndpoints/privateDnsZoneGroups' = 3
        'Microsoft.Network/privateEndpoints' = 4
        'Microsoft.Network/privateDnsZones/virtualNetworkLinks' = 5
        'Microsoft.Network/privateDnsZones' = 6
        'Microsoft.ContainerRegistry/registries' = 7
        'Microsoft.KeyVault/vaults' = 7
        'Microsoft.Insights/components' = 7
        'Microsoft.OperationalInsights/workspaces' = 8
        'Microsoft.ManagedIdentity/userAssignedIdentities' = 9
        'Microsoft.Network/virtualNetworks/subnets' = 10
        'Microsoft.Network/virtualNetworks' = 11
        'Microsoft.Network/natGateways' = 12
        'Microsoft.Network/publicIPAddresses' = 13
        'Microsoft.Authorization/roleDefinitions' = 14
        'Microsoft.Resources/deployments' = 15
    }
    foreach ($resource in @($live | Sort-Object { $priority[$_.type] })) {
        $phase = 'owned resource deletion'
        $subscription = $resource.id.Split('/')[2]
        if ($resource.type -ieq 'Microsoft.ContainerInstance/containerGroups') {
            $phase = 'evaluator container shutdown'
            $null = Invoke-EvaluatorAzJson @('container', 'stop', '--name', $resource.id.Split('/')[-1],
                '--resource-group', $manifest.resourceGroupName) $subscription $phase -TimeoutSeconds 600 -NoOutput
        }
        $target = @($manifest.resources | Where-Object { $_.id -ieq $resource.id })[0]
        $phase = 'owned resource deletion'
        $null = Invoke-EvaluatorAzJson @('resource', 'delete', '--ids', $target.id, '--api-version', $target.apiVersion) `
            $subscription $phase -TimeoutSeconds 1800 -NoOutput
        Write-Output "Deleted: $($target.id)"
    }
    $phase = 'final resource group inventory'
    $remaining = @(Get-EvaluatorGroupInventory $manifest)
    if (@($remaining | Where-Object { $owned.Contains($_.id) }).Count) { throw 'Owned resources remain after deletion; rerun after inspecting Azure state.' }
    if ($groupOwned -and $foreign.Count -eq 0 -and $remaining.Count -eq 0) {
        $groupState = Get-EvaluatorArmResource $group '2024-03-01'
        if ($groupState.tags['she-owner'] -cne $manifest.owner) { throw 'Resource group ownership changed during teardown.' }
        if ($PSCmdlet.ShouldProcess($group, 'Delete the empty rollout-created resource group')) {
            $null = Invoke-EvaluatorAzJson @('group', 'delete', '--name', $manifest.resourceGroupName, '--yes') `
                $manifest.subscriptionId 'resource group deletion' -TimeoutSeconds 3600 -NoOutput
            Write-Output "Deleted resource group: $($manifest.resourceGroupName)"
        }
    }
    else {
        Write-Output 'Retained resource group: it was not verifiably created by this rollout, or an initial/final inventory contains unrelated or remaining resources.'
    }
    Write-Output 'Teardown finished. Ownership manifest retained; Azure soft-delete retention and purge protection still apply.'
}
catch {
    [Console]::Error.WriteLine("Teardown failed ($phase). Verify the ownership manifest, Azure permissions and resource state, then rerun with the retained manifest. Legacy deployments cannot be safely inferred. No native output or configuration payloads are logged.")
    exit 1
}
