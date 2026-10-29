#Requires -Version 7.4
. (Join-Path $PSScriptRoot '..\..\scripts\delivery-common.ps1')
. (Join-Path $PSScriptRoot '..\..\scripts\environment.ps1')

function Assert-DemoInput {
    param([string] $SubscriptionId, [string] $ResourceGroupName, [string] $Location, [string] $NamePrefix)
    $guid = [guid]::Empty
    if (-not [guid]::TryParseExact($SubscriptionId, 'D', [ref]$guid) -or $guid -eq [guid]::Empty) {
        throw 'SubscriptionId must be a nonempty subscription GUID.'
    }
    if ($ResourceGroupName -cnotmatch '^[a-zA-Z0-9_().-]{1,90}\z' -or $ResourceGroupName.EndsWith('.')) {
        throw 'ResourceGroupName must be an explicit valid Azure resource group name.'
    }
    if ($Location -cnotmatch '^[a-z]+[0-9]*\z' -or $NamePrefix -cnotmatch '^[a-z0-9]{3,10}\z') {
        throw 'Use an Azure location code and a 3-10 character lowercase alphanumeric NamePrefix.'
    }
}

function Assert-DemoRootEndpoint {
    param([object] $Value)
    $uri = $null
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or
        -not [uri]::TryCreate($Value, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or -not $uri.Host -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        $uri.AbsolutePath -cne '/' -or $Value -match '[\s\\?#]' -or
        $Value -cnotmatch '^https://[^/]+/?\z') {
        throw 'A root HTTPS endpoint without credentials, query, fragment or extra path is required.'
    }
}

function Get-DemoTarget {
    param([Collections.IDictionary] $Manifest)
    $prefix = $Manifest.namePrefix
    $owner = $Manifest.owner
    $root = "/subscriptions/$($Manifest.subscriptionId)/resourceGroups/$($Manifest.resourceGroupName)/providers"
    foreach ($definition in @(
        @('Microsoft.ContainerInstance/containerGroups', "aci-$prefix-$owner", '2023-05-01', 'containerGroupResourceId'),
        @('Microsoft.CloudHealth/healthmodels', "hm-$prefix-$owner", '2026-10-01-preview', 'healthModelResourceId'),
        @('Microsoft.Insights/dataCollectionRules', "dcr-$prefix-$owner", '2023-03-11', 'dataCollectionRuleResourceId'),
        @('Microsoft.Insights/dataCollectionEndpoints', "dce-$prefix-$owner", '2023-03-11', 'dataCollectionEndpointResourceId'),
        @('Microsoft.Monitor/accounts', "amw-$prefix-$owner", '2023-04-03', 'workspaceResourceId'),
        @('Microsoft.ContainerRegistry/registries', "${prefix}${owner}acr", '2025-11-01', 'registryResourceId'),
        @('Microsoft.ManagedIdentity/userAssignedIdentities', "id-$prefix-$owner", '2023-01-31', 'identityResourceId')
    )) {
        @{ id = "$root/$($definition[0])/$($definition[1])"; type = $definition[0]; apiVersion = $definition[2]; field = $definition[3] }
    }
}

function Assert-DemoManifest {
    param([object] $Manifest)
    if ($Manifest -isnot [Collections.IDictionary] -or $Manifest.schemaVersion -isnot [long] -and
        $Manifest.schemaVersion -isnot [int] -or $Manifest.schemaVersion -ne 1) { throw 'Expected a schemaVersion 1 demo manifest.' }
    foreach ($field in @('subscriptionId', 'resourceGroupName', 'location', 'namePrefix', 'owner')) {
        if ($Manifest[$field] -isnot [string]) { throw "Missing manifest field: $field." }
    }
    Assert-DemoInput -SubscriptionId $Manifest.subscriptionId -ResourceGroupName $Manifest.resourceGroupName -Location $Manifest.location -NamePrefix $Manifest.namePrefix
    if ($Manifest.owner -cnotmatch '^[a-z0-9]{13}\z') { throw 'Invalid demo owner.' }
    $expected = @(Get-DemoTarget $Manifest)
    if ($Manifest.resources -isnot [array] -or $Manifest.resources.Count -ne $expected.Count) { throw 'Expected exactly seven owned top-level resource targets.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $Manifest.resources) {
        if ($resource -isnot [Collections.IDictionary] -or $resource.Count -ne 3 -or
            $resource.id -isnot [string] -or -not $ids.Add($resource.id)) { throw 'Invalid or duplicate manifest resource target.' }
        $target = @($expected | Where-Object { $_.id -ieq $resource.id })
        if ($target.Count -ne 1 -or $resource.type -ine $target[0].type -or $resource.apiVersion -cne $target[0].apiVersion) {
            throw 'Resource target does not match the exact demo subscription, group, type, name and API version.'
        }
    }
    foreach ($target in $expected) {
        if ($Manifest[$target.field] -isnot [string] -or $Manifest[$target.field] -ine $target.id) { throw "Invalid manifest $($target.field)." }
    }
    if ($Manifest.registryName -cne "$($Manifest.namePrefix)$($Manifest.owner)acr" -or
        $Manifest.identityName -cne "id-$($Manifest.namePrefix)-$($Manifest.owner)" -or
        $Manifest.containerGroupName -cne "aci-$($Manifest.namePrefix)-$($Manifest.owner)" -or
        $Manifest.registryLoginServer -isnot [string] -or $Manifest.registryLoginServer -cnotmatch '^[a-z0-9-]+\.azurecr\.io\z') {
        throw 'Invalid manifest registry, identity or container group name.'
    }
    foreach ($field in @('identityClientId', 'identityPrincipalId')) {
        $guid = [guid]::Empty
        if ($Manifest[$field] -isnot [string] -or -not [guid]::TryParseExact($Manifest[$field], 'D', [ref]$guid) -or $guid -eq [guid]::Empty) {
            throw "Invalid manifest $field."
        }
    }
    Assert-DemoRootEndpoint $Manifest.workspaceQueryEndpoint
    $uri = $null
    if ($Manifest.metricsIngestionEndpoint -isnot [string] -or
        -not [uri]::TryCreate($Manifest.metricsIngestionEndpoint, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -cne 'https' -or $uri.UserInfo -or $uri.Fragment -or
        $uri.AbsolutePath -cnotmatch '^/dataCollectionRules/[a-zA-Z0-9-]+/streams/Microsoft-PrometheusMetrics/api/v1/write\z' -or
        $uri.Query -cne '?api-version=2023-04-24') { throw 'Invalid manifest metrics ingestion endpoint.' }
    if ($Manifest.Contains('appImage') -and
        $Manifest.appImage -cnotmatch ('^' + [regex]::Escape($Manifest.registryLoginServer) + '/checkout-demo@sha256:[a-f0-9]{64}\z')) {
        throw 'Invalid immutable app image in manifest.'
    }
}

function Read-DemoManifest {
    param([string] $Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'ManifestPath must not be empty.' }
    $manifest = ConvertFrom-EvaluatorConfigurationJson (Get-Content -LiteralPath $Path -Raw)
    Assert-DemoManifest $manifest
    return $manifest
}

function Write-DemoJson {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal local artifact writer; mutating callers perform ShouldProcess first.')]
    param([string] $Path, [object] $Value)
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Output path must not be empty.' }
    $fullPath = [IO.Path]::GetFullPath($Path)
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fullPath))
    $pending = "$fullPath.$([guid]::NewGuid().ToString('N')).writing"
    try {
        [IO.File]::WriteAllText($pending, (ConvertTo-Json -InputObject $Value -Depth 64 -WarningAction Stop), [Text.UTF8Encoding]::new($false))
        [IO.File]::Move($pending, $fullPath, $true)
    }
    finally { if ([IO.File]::Exists($pending)) { [IO.File]::Delete($pending) } }
}

function Invoke-DemoAz {
    param([string[]] $Arguments, [string] $SubscriptionId, [string] $Operation, [int] $TimeoutSeconds = 120)
    Invoke-EvaluatorCommand -Command az -Arguments ($Arguments + @('--subscription', $SubscriptionId, '--only-show-errors', '--output', 'json')) `
        -Operation $Operation -TimeoutSeconds $TimeoutSeconds
}

function Assert-DemoCloud {
    param([string] $SubscriptionId)
    $account = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz -Arguments @('account', 'show') -SubscriptionId $SubscriptionId -Operation 'public cloud and subscription validation')
    if ($account.environmentName -cne 'AzureCloud' -or $account.id -ine $SubscriptionId -or $account.state -cne 'Enabled') {
        throw 'An enabled, explicitly selected Azure public-cloud subscription is required. Check az login and the selected cloud.'
    }
}

function Get-DemoLiveResource {
    param([string] $SubscriptionId, [string] $ResourceGroupName)
    $exists = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz -Arguments @('group', 'exists', '--name', $ResourceGroupName) -SubscriptionId $SubscriptionId -Operation 'resource group existence')
    if ($exists -isnot [bool]) { throw 'Invalid resource group existence response.' }
    if (-not $exists) { return }
    $json = Invoke-DemoAz -Arguments @('resource', 'list', '--resource-group', $ResourceGroupName) -SubscriptionId $SubscriptionId -Operation 'selected group resource inventory'
    $resources = (ConvertFrom-EvaluatorConfigurationJson ('{"items":' + $json + '}')).items
    if ($resources -isnot [array]) { throw 'Invalid resource inventory response.' }
    foreach ($resource in $resources) {
        if ($resource -isnot [Collections.IDictionary] -or $resource.id -isnot [string] -or $resource.type -isnot [string]) { throw 'Invalid live resource.' }
        $resource
    }
}

function Assert-DemoOwnership {
    param([object[]] $Targets, [AllowEmptyCollection()][object[]] $LiveResources, [string] $Owner)
    foreach ($target in $Targets) {
        $live = @($LiveResources | Where-Object { $_.id -ieq $target.id })
        if ($live.Count -gt 1) { throw 'Duplicate resource ID in live inventory.' }
        if ($live.Count -eq 1 -and ($live[0].type -ine $target.type -or
                $live[0].tags -isnot [Collections.IDictionary] -or $live[0].tags['she-demo-system'] -cne $Owner)) {
            throw "Ownership mismatch: $($target.id). Nothing may overwrite/delete foreign-owned resources."
        }
    }
}

function Assert-DemoMarker {
    param([string] $Output, [string] $Marker)
    # ACI exec decorates stdout with VT terminal mode/formatting sequences.
    $plainOutput = $Output -replace '\x1B(?:\[[0-?]*[ -/]*[@-~]|[=>])', ''
    if ($Marker -cnotin @($plainOutput -split '\r?\n' | ForEach-Object { $_.Trim() })) {
        throw 'Container command did not return its exact application success marker. Inspect container logs and retry explicitly.'
    }
}
