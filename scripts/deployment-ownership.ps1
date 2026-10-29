#Requires -Version 7.4
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
. (Join-Path $PSScriptRoot 'environment.ps1')

$script:EvaluatorResourceApis = @{
    'Microsoft.ContainerInstance/containerGroups' = '2025-09-01'
    'Microsoft.ContainerRegistry/registries' = '2023-07-01'
    'Microsoft.ManagedIdentity/userAssignedIdentities' = '2023-01-31'
    'Microsoft.OperationalInsights/workspaces' = '2023-09-01'
    'Microsoft.Insights/components' = '2020-02-02'
    'Microsoft.KeyVault/vaults' = '2023-07-01'
    'Microsoft.Network/publicIPAddresses' = '2024-05-01'
    'Microsoft.Network/natGateways' = '2024-05-01'
    'Microsoft.Network/virtualNetworks' = '2024-05-01'
    'Microsoft.Network/virtualNetworks/subnets' = '2024-05-01'
    'Microsoft.Network/privateEndpoints' = '2024-05-01'
    'Microsoft.Network/privateEndpoints/privateDnsZoneGroups' = '2024-05-01'
    'Microsoft.Network/privateDnsZones' = '2020-06-01'
    'Microsoft.Network/privateDnsZones/virtualNetworkLinks' = '2020-06-01'
    'Microsoft.Authorization/roleAssignments' = '2022-04-01'
    'Microsoft.Authorization/roleDefinitions' = '2022-04-01'
    'Microsoft.Resources/deployments' = '2024-03-01'
}

function Invoke-EvaluatorAzJson {
    param([string[]] $Arguments, [string] $SubscriptionId, [string] $Operation, [int] $TimeoutSeconds = 120, [switch] $NoOutput)
    $format = if ($NoOutput) { 'none' } else { 'json' }
    $json = Invoke-EvaluatorCommand az ($Arguments + @('--subscription', $SubscriptionId, '--only-show-errors', '--output', $format)) `
        $Operation -TimeoutSeconds $TimeoutSeconds
    if (-not $NoOutput) {
        $envelope = ConvertFrom-EvaluatorConfigurationJson ('{"result":' + $json + '}')
        return ,$envelope.result
    }
}

function Get-EvaluatorResourceType {
    param([string] $Id)
    $parts = ($Id -split '/providers/')[-1].Split('/')
    if ($parts.Count -lt 3 -or $parts.Count % 2 -ne 1) { throw 'Invalid owned resource ID.' }
    $type = $parts[0]
    for ($i = 1; $i -lt $parts.Count; $i += 2) { $type += '/' + $parts[$i] }
    return $type
}

function Get-EvaluatorParentId {
    param([string] $Id)
    $Id.Substring(0, $Id.LastIndexOf('/')).Substring(0, $Id.Substring(0, $Id.LastIndexOf('/')).LastIndexOf('/'))
}

function Read-EvaluatorManifest {
    param([string] $Path)
    if (-not [IO.File]::Exists($Path)) { throw 'An ownership manifest from the updated rollout is required; legacy deployments are not supported.' }
    $manifest = ConvertFrom-EvaluatorConfigurationJson (Get-Content -LiteralPath $Path -Raw)
    Assert-EvaluatorManifest $manifest
    return $manifest
}

function Assert-EvaluatorManifest {
    param([object] $Manifest)
    if ($Manifest -isnot [Collections.IDictionary] -or $Manifest.schemaVersion -ne 1 -or
        $Manifest.resourceGroupCreated -isnot [bool] -or $Manifest.resources -isnot [array] -or
        $Manifest.healthModelResourceIds -isnot [array]) { throw 'Invalid ownership manifest.' }
    if ($Manifest.Contains('localContainerCleanup') -and $Manifest.localContainerCleanup -isnot [bool]) { throw 'Invalid local cleanup ownership.' }
    foreach ($field in @('subscriptionId', 'owner')) {
        $guid = [guid]::Empty
        if ($Manifest[$field] -isnot [string] -or -not [guid]::TryParseExact($Manifest[$field], 'D', [ref]$guid) -or
            $guid -eq [guid]::Empty) { throw 'Invalid manifest identity.' }
    }
    if ($Manifest.resourceGroupName -cnotmatch '^[a-zA-Z0-9_-]{1,90}\z' -or
        $Manifest.namePrefix -cnotmatch '^[a-z][a-z0-9]{2,9}\z') { throw 'Invalid manifest deployment target.' }
    $group = "/subscriptions/$($Manifest.subscriptionId)/resourceGroups/$($Manifest.resourceGroupName)"
    $scopes = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($model in $Manifest.healthModelResourceIds) {
        if ($model -isnot [string] -or $model -inotmatch '^/subscriptions/[a-f0-9-]{36}/resourceGroups/[a-z0-9_.()-]{1,90}/providers/Microsoft\.CloudHealth/healthmodels/[a-zA-Z0-9-]+\z') {
            throw 'Invalid manifest model scope.'
        }
        $null = $scopes.Add($model)
        $null = $scopes.Add(($model -split '/providers/')[0])
    }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $Manifest.resources) {
        if ($resource -isnot [Collections.IDictionary] -or $resource.id -isnot [string] -or
            $resource.id -cnotmatch '^/subscriptions/[a-fA-F0-9-]{36}/(?:resourceGroups/[a-zA-Z0-9_.()-]+/)?providers/[a-zA-Z0-9_./@()-]+\z' -or
            @($resource.id.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -or -not $ids.Add($resource.id)) {
            throw 'Invalid or duplicate owned resource.'
        }
        $type = Get-EvaluatorResourceType $resource.id
        if (-not $script:EvaluatorResourceApis.Contains($type) -or $resource.type -ine $type -or
            $resource.apiVersion -cne $script:EvaluatorResourceApis[$type]) { throw 'Unsupported owned resource type or API.' }
        $local = $resource.id.StartsWith("$group/providers/", [StringComparison]::OrdinalIgnoreCase)
        $subscriptionScope = "/subscriptions/$($Manifest.subscriptionId)/providers/$type/"
        $externalScope = ($resource.id -split '/providers/Microsoft\.(?:Authorization|Resources)/')[0]
        if (-not $local -and -not (
                $type -in @('Microsoft.Authorization/roleDefinitions', 'Microsoft.Resources/deployments') -and
                $resource.id.StartsWith($subscriptionScope, [StringComparison]::OrdinalIgnoreCase)
            ) -and -not (
                $type -in @('Microsoft.Authorization/roleAssignments', 'Microsoft.Resources/deployments') -and $scopes.Contains($externalScope)
            )) { throw 'Owned resource lies outside the permitted deployment scopes.' }
    }
}

function Write-EvaluatorManifest {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Atomic ownership journal; the rollout caller authorizes mutations before writing.')]
    param([string] $Path, [Collections.IDictionary] $Manifest)
    Assert-EvaluatorManifest $Manifest
    $fullPath = [IO.Path]::GetFullPath($Path)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath))
    $pending = "$fullPath.$([guid]::NewGuid().ToString('N')).writing"
    try {
        [IO.File]::WriteAllText($pending, (ConvertTo-Json -InputObject $Manifest -Depth 32 -WarningAction Stop), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($pending, $fullPath, $true)
    }
    finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
}

function Get-EvaluatorArmResource {
    param([string] $Id, [string] $ApiVersion)
    $subscription = $Id.Split('/')[2]
    $value = Invoke-EvaluatorAzJson @('rest', '--method', 'get', '--url', "https://management.azure.com${Id}?api-version=$ApiVersion") `
        $subscription 'ownership inspection'
    if ($value -isnot [Collections.IDictionary] -or $value.id -ine $Id) { throw 'Invalid resource ownership response.' }
    return $value
}

function Get-EvaluatorArmCollection {
    param([string] $Id, [string] $ApiVersion)
    $subscription = $Id.Split('/')[2]
    $url = "https://management.azure.com${Id}?api-version=$ApiVersion"
    $visited = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($url) {
        if (-not $url.StartsWith("https://management.azure.com/subscriptions/$subscription/", [StringComparison]::OrdinalIgnoreCase) -or
            -not $visited.Add($url)) { throw 'Invalid ARM inventory continuation.' }
        $page = Invoke-EvaluatorAzJson @('rest', '--method', 'get', '--url', $url) $subscription 'ownership inventory'
        if ($page -isnot [Collections.IDictionary] -or $page.value -isnot [array]) { throw 'Invalid ARM resource inventory.' }
        foreach ($value in $page.value) {
            if ($value -isnot [Collections.IDictionary] -or $value.id -isnot [string]) { throw 'Invalid inventory entry.' }
            $value
        }
        $url = $page.nextLink
    }
}

function Get-EvaluatorGroupInventory {
    param([Collections.IDictionary] $Manifest)
    $subscription = $Manifest.subscriptionId
    $groupName = $Manifest.resourceGroupName
    $exists = Invoke-EvaluatorAzJson @('group', 'exists', '--name', $groupName) $subscription 'resource group existence'
    if ($exists -isnot [bool]) { throw 'Invalid resource group existence response.' }
    if (-not $exists) { return }
    $resources = Invoke-EvaluatorAzJson @('resource', 'list', '--resource-group', $groupName) $subscription 'resource group inventory'
    if ($resources -isnot [array]) { throw 'Invalid resource group inventory.' }
    $deployments = Invoke-EvaluatorAzJson @('deployment', 'group', 'list', '--resource-group', $groupName,
        '--query', '[].{id:id,type:type}') $subscription 'resource group deployment inventory'
    if ($deployments -isnot [array]) { throw 'Invalid deployment inventory.' }
    $group = "/subscriptions/$subscription/resourceGroups/$groupName"
    # ARM also returns inherited assignments; only group-local grants belong in its deletion inventory.
    $assignments = @(Get-EvaluatorArmCollection "$group/providers/Microsoft.Authorization/roleAssignments" '2022-04-01' |
        Where-Object { $_.id.StartsWith("$group/providers/", [StringComparison]::OrdinalIgnoreCase) })
    $all = @($resources) + @($deployments) + $assignments
    foreach ($resource in $resources) {
        if ($resource.id -inotin $Manifest.resources.id) { continue }
        $collections = switch ($resource.type) {
            'Microsoft.Network/virtualNetworks' { 'subnets' }
            'Microsoft.Network/privateEndpoints' { 'privateDnsZoneGroups' }
            'Microsoft.Network/privateDnsZones' { 'virtualNetworkLinks'; 'ALL' }
        }
        foreach ($collection in $collections) {
            $all += @(Get-EvaluatorArmCollection "$($resource.id)/$collection" $script:EvaluatorResourceApis[$resource.type])
        }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $all) {
        if ($resource -isnot [Collections.IDictionary] -or $resource.id -isnot [string] -or
            -not $resource.id.StartsWith("$group/providers/", [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid group inventory entry.' }
        if ($seen.Add($resource.id)) { $resource }
    }
}

function Get-EvaluatorOwnedLiveResource {
    param([Collections.IDictionary] $Manifest, [AllowEmptyCollection()][object[]] $GroupInventory)
    $present = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $GroupInventory) { $null = $present.Add($resource.id) }
    $group = "/subscriptions/$($Manifest.subscriptionId)/resourceGroups/$($Manifest.resourceGroupName)"
    foreach ($model in $Manifest.healthModelResourceIds) {
        foreach ($resource in @(Get-EvaluatorArmCollection "$model/providers/Microsoft.Authorization/roleAssignments" '2022-04-01')) {
            $null = $present.Add($resource.id)
        }
    }
    foreach ($resource in $Manifest.resources) {
        if ($resource.type -ieq 'Microsoft.Authorization/roleDefinitions') {
            $roles = Invoke-EvaluatorAzJson @('role', 'definition', 'list', '--name', $resource.id.Split('/')[-1]) `
                $Manifest.subscriptionId 'custom role existence'
            if ($roles -isnot [array]) { throw 'Invalid custom role inventory.' }
            foreach ($role in $roles) { if ($role.id -ieq $resource.id) { $null = $present.Add($role.id) } }
        }
        elseif ($resource.type -ieq 'Microsoft.Resources/deployments' -and
            -not $resource.id.StartsWith("$group/", [StringComparison]::OrdinalIgnoreCase)) {
            $scope = Get-EvaluatorParentId $resource.id
            $scope = $scope.Substring(0, $scope.LastIndexOf('/providers', [StringComparison]::OrdinalIgnoreCase))
            $arguments = if ($scope -imatch '^/subscriptions/[^/]+$') { @('deployment', 'sub', 'list') } else {
                @('deployment', 'group', 'list', '--resource-group', $scope.Split('/')[4])
            }
            $deployments = Invoke-EvaluatorAzJson ($arguments + @('--query', '[].{id:id,type:type}')) `
                $scope.Split('/')[2] 'deployment journal inventory'
            if ($deployments -isnot [array]) { throw 'Invalid deployment journal inventory.' }
            foreach ($deployment in $deployments) { if ($deployment.id -ieq $resource.id) { $null = $present.Add($deployment.id) } }
        }
    }
    foreach ($resource in $Manifest.resources) {
        if ($present.Contains($resource.id)) {
            $live = Get-EvaluatorArmResource $resource.id $resource.apiVersion
            if ($live.type -and $live.type -ine $resource.type) { throw 'Resource type changed since rollout.' }
            $live
        }
    }
}

function Assert-EvaluatorLiveOwnership {
    param([Collections.IDictionary] $Manifest, [AllowEmptyCollection()][object[]] $LiveResources)
    $byId = @{}
    foreach ($resource in $LiveResources) { $byId[$resource.id] = $resource }
    $identity = @($LiveResources | Where-Object { $_.type -ieq 'Microsoft.ManagedIdentity/userAssignedIdentities' })
    $principal = if ($identity.Count -eq 1) { $identity[0].properties.principalId } else { $Manifest.identityPrincipalId }
    if ($Manifest.identityPrincipalId -and $principal -ine $Manifest.identityPrincipalId) { throw 'Evaluator identity changed since rollout.' }
    foreach ($resource in $LiveResources) {
        $owned = $false
        switch ($resource.type) {
            'Microsoft.Authorization/roleDefinitions' {
                $owned = $resource.properties.type -ceq 'CustomRole' -and
                    $resource.properties.description -ceq "Self-hosted evaluators owner $($Manifest.owner)"
            }
            'Microsoft.Authorization/roleAssignments' {
                $scope = $resource.id.Substring(0, $resource.id.LastIndexOf('/providers/', [StringComparison]::OrdinalIgnoreCase))
                if ($scope -iin $Manifest.healthModelResourceIds) {
                    $roles = @($Manifest.resources | Where-Object { $_.type -ieq 'Microsoft.Authorization/roleDefinitions' })
                    $contributor = "/subscriptions/$($scope.Split('/')[2])/providers/Microsoft.Authorization/roleDefinitions/b24988ac-6180-42a0-ab88-20f7382dd24c"
                    $owned = $principal -and $resource.properties.principalId -ieq $principal -and
                        $resource.properties.scope -ieq $scope -and
                        ($resource.properties.roleDefinitionId -ieq $contributor -or $resource.properties.roleDefinitionId -iin $roles.id)
                }
                elseif ($byId.Contains($scope)) { $owned = $byId[$scope].tags['she-owner'] -ceq $Manifest.owner }
            }
            'Microsoft.Resources/deployments' {
                $parameters = $resource.properties.parameters
                $owned = ($parameters.tags.value -is [Collections.IDictionary] -and
                    $parameters.tags.value['she-owner'] -ceq $Manifest.owner) -or $parameters.owner.value -ceq $Manifest.owner
            }
            { $_ -in @('Microsoft.Network/privateDnsZones/virtualNetworkLinks', 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups',
                    'Microsoft.Network/virtualNetworks/subnets') } {
                $parent = Get-EvaluatorParentId $resource.id
                $owned = $byId.Contains($parent) -and $byId[$parent].tags['she-owner'] -ceq $Manifest.owner
            }
            default { $owned = $resource.tags -is [Collections.IDictionary] -and $resource.tags['she-owner'] -ceq $Manifest.owner }
        }
        if (-not $owned) { throw 'Live resource ownership does not match the rollout manifest.' }
    }
}

function Get-EvaluatorManagedResourceId {
    param([Collections.IDictionary] $Manifest, [AllowEmptyCollection()][object[]] $LiveResources)
    foreach ($resource in $LiveResources) {
        if ($resource.type -ieq 'Microsoft.Network/privateEndpoints') {
            foreach ($nic in $resource.properties.networkInterfaces) {
                if ($nic.id -isnot [string] -or $nic.id -inotmatch '/providers/Microsoft\.Network/networkInterfaces/[^/]+\z') {
                    throw 'Invalid private endpoint network interface ownership.'
                }
                $nic.id
            }
        }
        elseif ($resource.type -ieq 'Microsoft.Network/privateDnsZones') {
            "$($resource.id)/SOA/@"
        }
        elseif ($resource.type -ieq 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups') {
            foreach ($configuration in $resource.properties.privateDnsZoneConfigs) {
                $zone = $configuration.properties.privateDnsZoneId
                if ($zone -inotin $Manifest.resources.id) { throw 'Private DNS configuration references an unowned zone.' }
                foreach ($record in $configuration.properties.recordSets) {
                    if ($record.recordType -cnotin @('A', 'AAAA', 'CNAME', 'TXT') -or
                        $record.recordSetName -cnotmatch '^[a-zA-Z0-9_.@*-]+\z') { throw 'Invalid generated private DNS record ownership.' }
                    "$zone/$($record.recordType)/$($record.recordSetName)"
                }
            }
        }
    }
}

function Get-EvaluatorLocalContainer {
    param([Collections.IDictionary] $Manifest)
    if (-not $Manifest.localContainerCleanup) { return }
    $text = Invoke-EvaluatorCommand docker @('ps', '--all', '--quiet', '--no-trunc', '--filter', "label=she.rollout=$($Manifest.owner)") `
        'local rollout container inventory' -TimeoutSeconds 60
    foreach ($id in @($text -split '\r?\n' | Where-Object { $_ })) {
        if ($id -cnotmatch '^[a-f0-9]{64}\z') { throw 'Invalid local container ID.' }
        $json = Invoke-EvaluatorCommand docker @('inspect', '--type', 'container', $id) 'local container ownership' -TimeoutSeconds 60
        $envelope = ConvertFrom-EvaluatorConfigurationJson ('{"containers":' + $json + '}')
        $containers = $envelope.containers
        if ($containers -isnot [array] -or $containers.Count -ne 1 -or $containers[0].Id -cne $id -or
            $containers[0].Config.Labels['she.rollout'] -cne $Manifest.owner -or $containers[0].State.Running -isnot [bool]) {
            throw 'Local container ownership does not match the rollout manifest.'
        }
        $containers[0]
    }
}

function Add-EvaluatorDeploymentPlan {
    param([Collections.IDictionary] $Manifest, [Collections.IDictionary] $Desired, [object] $Preview, [string] $Phase)
    if ($Preview -isnot [Collections.IDictionary] -or $Preview.status -cne 'Succeeded' -or $Preview.changes -isnot [array]) {
        throw 'A complete successful Azure what-if inventory is required for ownership tracking.'
    }
    $group = "/subscriptions/$($Manifest.subscriptionId)/resourceGroups/$($Manifest.resourceGroupName)"
    $planned = [Collections.Generic.List[object]]::new()
    foreach ($change in $Preview.changes) {
        if ($change.changeType -ceq 'Ignore') { continue }
        if ($change.resourceId -ieq $group) { continue }
        if ($change.changeType -cnotin @('Create', 'Modify', 'NoChange', 'Deploy')) { throw 'Unresolved or destructive Azure what-if change.' }
        $type = Get-EvaluatorResourceType $change.resourceId
        if (-not $script:EvaluatorResourceApis.Contains($type)) { throw 'Unexpected rollout resource type.' }
        if ($type -ieq 'Microsoft.Authorization/roleDefinitions') { throw 'New rollouts must use existing built-in role definitions.' }
        if ($change.resourceId -inotin $Manifest.resources.id -and $change.changeType -cne 'Create') {
            throw 'The rollout may not adopt pre-existing resources or legacy deployments.'
        }
        $planned.Add(@{ id = $change.resourceId; type = $type; apiVersion = $script:EvaluatorResourceApis[$type] })
        if ($type -ieq 'Microsoft.Network/virtualNetworks') {
            $subnets = if ($Desired.networkMode -ceq 'private') { @('aci', 'endpoints') } else { @('aci') }
            foreach ($subnet in $subnets) {
                $planned.Add(@{ id = "$($change.resourceId)/subnets/$subnet"; type = "$type/subnets"; apiVersion = '2024-05-01' })
            }
        }
        elseif ($type -ieq 'Microsoft.Network/privateEndpoints') {
            $planned.Add(@{ id = "$($change.resourceId)/privateDnsZoneGroups/default"; type = "$type/privateDnsZoneGroups"; apiVersion = '2024-05-01' })
        }
        elseif ($type -ieq 'Microsoft.Network/privateDnsZones') {
            $planned.Add(@{ id = "$($change.resourceId)/virtualNetworkLinks/$($Desired.namePrefix)-vnet"; type = "$type/virtualNetworkLinks"; apiVersion = '2020-06-01' })
        }
    }
    $modules = @('identity', 'registry', 'monitoring', 'keyvault')
    if ($Desired.networkMode -cne 'public') { $modules += 'networking' }
    if ($Desired.deployContainerGroup) { $modules += 'container-group' }
    $deploymentIds = @("/subscriptions/$($Manifest.subscriptionId)/providers/Microsoft.Resources/deployments/$($Desired.namePrefix)-$Phase")
    $deploymentIds += @($modules | ForEach-Object { "$group/providers/Microsoft.Resources/deployments/$($Desired.namePrefix)-$_" })
    for ($i = 0; $i -lt $Desired.healthModelResourceIds.Count; $i++) {
        $modelGroup = ($Desired.healthModelResourceIds[$i] -split '/providers/')[0]
        $deploymentIds += "$modelGroup/providers/Microsoft.Resources/deployments/$($Desired.namePrefix)-model-access-$i"
    }
    foreach ($id in $deploymentIds) { $planned.Add(@{ id = $id; type = 'Microsoft.Resources/deployments'; apiVersion = '2024-03-01' }) }
    $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $Manifest.resources) { $null = $known.Add($resource.id) }
    foreach ($resource in $planned) { if ($known.Add($resource.id)) { $Manifest.resources += $resource } }
    Assert-EvaluatorManifest $Manifest
    $minimumCounts = @{
        'Microsoft.ManagedIdentity/userAssignedIdentities' = 1
        'Microsoft.ContainerRegistry/registries' = 1
        'Microsoft.OperationalInsights/workspaces' = 1
        'Microsoft.Insights/components' = 1
        'Microsoft.KeyVault/vaults' = 1
        'Microsoft.Authorization/roleAssignments' = 4 + $Desired.adminPrincipalIds.Count + $Desired.healthModelResourceIds.Count
    }
    if ($Desired.deployContainerGroup) { $minimumCounts['Microsoft.ContainerInstance/containerGroups'] = 1 }
    if ($Desired.networkMode -cne 'public') {
        foreach ($type in @('Microsoft.Network/virtualNetworks', 'Microsoft.Network/natGateways', 'Microsoft.Network/publicIPAddresses')) {
            $minimumCounts[$type] = 1
        }
    }
    if ($Desired.networkMode -ceq 'private') {
        $minimumCounts['Microsoft.Network/privateEndpoints'] = 2
        $minimumCounts['Microsoft.Network/privateDnsZones'] = 2
    }
    foreach ($type in $minimumCounts.Keys) {
        if (@($Manifest.resources | Where-Object { $_.type -ieq $type }).Count -lt $minimumCounts[$type]) {
            throw 'Azure what-if did not enumerate the complete rollout resource inventory.'
        }
    }
    foreach ($model in $Desired.healthModelResourceIds) {
        if (-not @($Manifest.resources | Where-Object {
                    $_.id.StartsWith("$model/providers/Microsoft.Authorization/roleAssignments/", [StringComparison]::OrdinalIgnoreCase)
                }).Count) { throw 'Azure what-if did not enumerate every model-scoped assignment.' }
    }
}
