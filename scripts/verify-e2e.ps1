#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Mapped')]
param(
    [Parameter(Mandatory)][string] $ExpectedFile,
    [Parameter(Mandatory, ParameterSetName = 'Mapped')][string] $SnapshotMappingFile,
    [Parameter(Mandatory, ParameterSetName = 'CloudHealthDiagnostics')][switch] $CloudHealthDiagnostics,
    [Parameter(Mandatory)][guid] $ExpectedPrincipalId,
    [Parameter(Mandatory)][guid] $ExpectedTenantId,
    [Parameter(Mandatory)][datetimeoffset] $NotBefore,
    [ValidateRange(1, 1800)][int] $TimeoutSeconds = 180,
    [ValidateRange(0, 60)][int] $PollSeconds = 5,
    [switch] $AuthorizeReadback
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
. (Join-Path $PSScriptRoot 'environment.ps1')
$phase = 'authorization and input'
$clock = [Diagnostics.Stopwatch]::StartNew()
$deadline = [timespan]::FromSeconds($TimeoutSeconds)
$tokens = @{}

function Get-ReadbackTimeBudget {
    $remaining = $deadline.TotalSeconds - $clock.Elapsed.TotalSeconds
    if ($remaining -le 0) { throw 'Readback deadline exceeded.' }
    return [math]::Max(1, [math]::Min(20, [math]::Ceiling($remaining)))
}

function ConvertFrom-ReadbackObject {
    param([string] $Json)
    $document = [Text.Json.JsonDocument]::Parse($Json)
    try {
        # Check the JSON root before PowerShell can unwrap a one-element array.
        if ($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw 'Expected a readback object.' }
    }
    finally { $document.Dispose() }
    return ConvertFrom-EvaluatorConfigurationJson $Json
}

function Get-ReadbackToken {
    param([string] $Audience, [string] $Subscription)
    $json = Invoke-EvaluatorCommand az @('account', 'get-access-token', '--subscription', $Subscription,
        '--scope', "$Audience/.default", '--only-show-errors', '--output', 'json') 'readback credential' `
        -TimeoutSeconds (Get-ReadbackTimeBudget)
    $credential = ConvertFrom-EvaluatorConfigurationJson $json
    $token = $credential.accessToken
    if ($token -isnot [string] -or $token.Length -gt 65536) { throw 'Invalid token.' }
    $parts = $token.Split('.')
    if ($parts.Length -ne 3) { throw 'Invalid token.' }
    $encoded = $parts[1].Replace('-', '+').Replace('_', '/')
    $encoded = $encoded.PadRight($encoded.Length + (4 - $encoded.Length % 4) % 4, '=')
    $claims = ConvertFrom-EvaluatorConfigurationJson ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($encoded)))
    # This is an identity safety check on a token acquired from Azure CLI, not signature validation.
    # The service remains responsible for validating the token.
    $audienceMatches = $claims.aud -is [string] -and
        ($claims.aud.TrimEnd('/') -ceq $Audience -or
            ($Audience -ceq 'https://data.cloudhealth.azure.com' -and
                $claims.aud -ceq '36e21f15-cda0-45c2-9932-98f28a8bdb28'))
    if ($claims.oid -ine $ExpectedPrincipalId.ToString() -or $claims.tid -ine $ExpectedTenantId.ToString() -or
        -not $audienceMatches -or
        [DateTimeOffset]::FromUnixTimeSeconds([long]$claims.exp) -lt [DateTimeOffset]::UtcNow.AddSeconds($deadline.TotalSeconds)) {
        throw 'The selected credential is not the explicitly authorized identity, tenant or audience, or expires too soon.'
    }
    return $token
}

function Get-ReadbackJson {
    param([string] $Url, [string] $Audience)
    $response = Invoke-WebRequest -Uri $Url -Method Get -Headers @{Authorization = 'Bearer ' + $tokens[$Audience]} `
        -TimeoutSec (Get-ReadbackTimeBudget) -MaximumRedirection 0 -SkipHttpErrorCheck
    $null = Get-ReadbackTimeBudget
    if ([int]$response.StatusCode -ne 200) { throw 'Readback HTTP request failed.' }
    return ConvertFrom-ReadbackObject $response.Content
}

function Get-SnapshotValue {
    param([AllowNull()][object] $Value, [string] $Pointer)
    if ($Pointer -eq '') { return ,$Value }
    if (-not $Pointer.StartsWith('/')) { throw 'Expected a JSON pointer.' }
    foreach ($part in $Pointer.Substring(1).Split('/')) {
        $key = $part.Replace('~1', '/').Replace('~0', '~')
        if ($Value -is [Collections.IDictionary] -and $Value.Contains($key)) { $Value = $Value[$key] }
        elseif ($Value -is [array] -and $key -cmatch '^(0|[1-9][0-9]*)\z' -and [long]$key -lt $Value.Length) { $Value = $Value[[int]$key] }
        else { throw 'Snapshot does not match the supplied versioned contract mapping.' }
    }
    return ,$Value
}

function ConvertTo-SnapshotInstant {
    [OutputType([datetimeoffset])]
    param([AllowNull()][object] $Value)
    # PowerShell 7.4 JSON parsing materializes dates. Never round-trip them through object.ToString().
    if ($Value -is [datetimeoffset]) { return $Value }
    if ($Value -is [datetime] -and $Value.Kind -ne [DateTimeKind]::Unspecified) {
        return [datetimeoffset]::new($Value)
    }
    if ($Value -is [string] -and $Value -cmatch '^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z') {
        return [datetimeoffset]::Parse($Value, [Globalization.CultureInfo]::InvariantCulture,
            [Globalization.DateTimeStyles]::None)
    }
    throw 'Snapshot timestamps require an explicit UTC or numeric offset.'
}

function Test-EntitySnapshot {
    [OutputType([bool])]
    param([Collections.IDictionary] $Snapshot, [Collections.IDictionary] $Entity, [Collections.IDictionary] $Mapping)
    $signals = Get-SnapshotValue $Snapshot $Mapping.signals
    if ($signals -isnot [array] -or $signals.Count -ne $Entity.signals.Count) { return $false }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($signal in $signals) {
        $name = Get-SnapshotValue $signal $Mapping.name
        if ($name -isnot [string] -or -not $seen.Add($name)) { return $false }
        $expected = @($Entity.signals | Where-Object { $_.name -ceq $name })
        if ($expected.Count -ne 1 -or (Get-SnapshotValue $signal $Mapping.state) -cne $expected[0].healthState) { return $false }
        $reported = ConvertTo-SnapshotInstant (Get-SnapshotValue $signal $Mapping.reportedAt)
        $expires = ConvertTo-SnapshotInstant (Get-SnapshotValue $signal $Mapping.expiresAt)
        if ($reported -lt $NotBefore -or $reported -gt [datetimeoffset]::UtcNow.AddSeconds(5) -or
            $expires -le [datetimeoffset]::UtcNow -or
            [math]::Abs(($expires - $reported).TotalSeconds - 60 * $expected[0].expiresInMinutes) -gt 1) { return $false }
    }
    return $true
}

function Test-ReadbackNumber {
    [OutputType([bool])]
    param([AllowNull()][object] $Value)
    return ($Value -is [long] -or $Value -is [int] -or $Value -is [double] -or $Value -is [decimal]) -and
        [double]::IsFinite([double]$Value)
}

function Get-CloudHealthExternalSignal {
    param([Collections.IDictionary] $Properties)
    $signals = @()
    if ($Properties.Contains('signalGroups')) {
        $groups = $Properties.signalGroups
        if ($groups -isnot [Collections.IDictionary]) { throw 'Invalid ARM signal groups.' }
        if ($groups.Contains('external')) {
            $external = $groups.external
            if ($external -isnot [Collections.IDictionary]) { throw 'Invalid ARM external signal group.' }
            if ($external.Contains('signals')) {
                $signals = $external.signals
                if ($signals -isnot [array]) { throw 'Invalid ARM external signal array.' }
            }
        }
    }
    return ,$signals
}

function Test-CloudHealthEntity {
    [OutputType([bool])]
    param([Collections.IDictionary] $Snapshot, [Collections.IDictionary] $Entity,
        [Collections.IDictionary] $Model, [switch] $ArmStatus)
    if ($ArmStatus) {
        if ($Snapshot.id -isnot [string] -or $Snapshot.id -ine "$($Model.resourceId)/entities/$($Entity.name)" -or
            $Snapshot.name -isnot [string] -or $Snapshot.name -cne $Entity.name) { return $false }
        $properties = $Snapshot.properties
        if ($properties -isnot [Collections.IDictionary]) { throw 'Invalid ARM entity properties.' }
        $state = $properties.healthState
        $signals = Get-CloudHealthExternalSignal $properties
    }
    else {
        if ($Snapshot.entityName -isnot [string] -or $Snapshot.entityName -cne $Entity.name) { return $false }
        $state = $Snapshot.healthState
        $signals = $Snapshot.signalInputs
        if ($signals -isnot [array]) { throw 'Invalid diagnostic signal inputs.' }
    }
    if ($state -isnot [string] -or $state -cne $Entity.healthState -or $signals.Count -ne $Entity.signals.Count) { return $false }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($signal in $signals) {
        if ($signal -isnot [Collections.IDictionary] -or $signal.name -isnot [string] -or -not $seen.Add($signal.name)) { return $false }
        $expectedMatches = @($Entity.signals | Where-Object { $_.name -ceq $signal.name })
        if ($expectedMatches.Count -ne 1) { return $false }
        $expectedSignal = $expectedMatches[0]
        if ($ArmStatus) {
            $status = $signal.status
            if ($status -isnot [Collections.IDictionary]) { throw 'Invalid ARM signal status.' }
            if ($status.healthState -isnot [string] -or $status.healthState -cne $expectedSignal.healthState) { return $false }
            if ($expectedSignal.Contains('value') -and
                (-not (Test-ReadbackNumber $status.value) -or [double]$status.value -ne [double]$expectedSignal.value)) { return $false }
            $reported = ConvertTo-SnapshotInstant $status.reportedAt
            if ($reported -lt $NotBefore -or $reported -gt [datetimeoffset]::UtcNow.AddSeconds(5)) { return $false }
        }
        elseif ($signal.effectiveHealthState -isnot [string] -or $signal.effectiveHealthState -cne $expectedSignal.healthState) { return $false }
    }
    return $true
}

try {
    if (-not $AuthorizeReadback) { throw 'Explicit authorization is required before credential acquisition or HTTP requests.' }
    if ($ExpectedPrincipalId -eq [guid]::Empty -or $ExpectedTenantId -eq [guid]::Empty -or
        $NotBefore -gt [datetimeoffset]::UtcNow) { throw 'Explicit identity and rollout lower-bound time are required.' }
    $expected = ConvertFrom-ReadbackObject (Get-Content -LiteralPath $ExpectedFile -Raw)
    if ($expected -isnot [Collections.IDictionary]) { throw 'Invalid expected state.' }
    if (-not $CloudHealthDiagnostics) {
        $mapping = ConvertFrom-ReadbackObject (Get-Content -LiteralPath $SnapshotMappingFile -Raw)
        if ($mapping -isnot [Collections.IDictionary] -or
            (@($mapping.Keys | Sort-Object) -join ',') -cne 'expiresAt,name,reportedAt,signals,state') { throw 'Invalid mapping.' }
        foreach ($value in $mapping.Values) {
            if ($value -isnot [string] -or $value.Length -gt 1024 -or $value -cnotmatch '^(?:/(?:[^~\x00-\x1f]|~[01])*)*\z') { throw 'Invalid JSON pointer.' }
        }
    }
    foreach ($key in @('containerGroupId', 'identityResourceId', 'registryResourceId', 'vaultResourceId')) {
        if ($expected[$key] -isnot [string] -or $expected[$key] -cnotmatch
            '^/subscriptions/[a-fA-F0-9-]{36}/resourceGroups/[a-zA-Z0-9_.()-]+/providers/[a-zA-Z0-9.]+/[a-zA-Z]+/[a-zA-Z0-9_-]+\z') { throw 'Invalid resource ID.' }
    }
    $subscription = $expected.containerGroupId.Split('/')[2]
    $null = [guid]::Parse($subscription)
    Assert-EvaluatorImageReference $expected.imageReference
    if ($expected.imageReference -cnotmatch '^[a-z0-9]+\.azurecr\.io/[a-z0-9._/-]+@sha256:[a-f0-9]{64}\z' -or
        $expected.networkMode -cnotin @('public', 'vnetNat', 'private') -or
        $expected.models -isnot [array] -or $expected.models.Count -eq 0) { throw 'Invalid image, network mode or models.' }
    $defaultPublicNetworkAccess = if ($expected.networkMode -ceq 'private') { 'Disabled' } else { 'Enabled' }
    $vaultPublicNetworkAccess = if ($expected.Contains('keyVaultPublicNetworkAccess')) { $expected.keyVaultPublicNetworkAccess } else { $defaultPublicNetworkAccess }
    if ($vaultPublicNetworkAccess -isnot [string] -or $vaultPublicNetworkAccess -cnotin @('Enabled', 'Disabled') -or
        ($expected.networkMode -ceq 'private' -and $vaultPublicNetworkAccess -cne 'Disabled')) { throw 'Invalid expected Key Vault public network access.' }
    if ($expected.networkMode -cne 'public') {
        foreach ($key in @('subnetId', 'natGatewayId')) {
            if ($expected[$key] -isnot [string] -or $expected[$key] -notmatch '^/subscriptions/[a-f0-9-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/[a-zA-Z]+/[^/?#]+(?:/subnets/[^/?#]+)?\z') { throw 'Invalid network resource ID.' }
        }
    }
    $entityCount = 0; $signalCount = 0; $emptyEntityCount = 0
    $origins = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $modelIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($model in $expected.models) {
        if ($CloudHealthDiagnostics) {
            if ($model -isnot [Collections.IDictionary] -or $model.resourceId -isnot [string] -or
                $model.resourceId -inotmatch '^/subscriptions/([a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12})/resourceGroups/[a-zA-Z0-9_.()-]+/providers/Microsoft\.CloudHealth/healthmodels/[a-zA-Z0-9_-]+\z') { throw 'Invalid explicit model resource ID.' }
            if ($Matches[1] -ine $subscription -or -not $modelIds.Add($model.resourceId) -or
                $model.apiVersion -cnotin @('2026-06-01-preview', '2026-10-01-preview') -or
                $model.controlPlaneApiVersion -cne '2026-10-01-preview' -or
                $model.dataPlaneUri -isnot [string]) { throw 'Invalid model subscription, version or origin.' }
        }
        $uri = [uri]$model.dataPlaneUri
        if (-not $uri.IsAbsoluteUri -or $uri.Scheme -cne 'https' -or $uri.AbsolutePath -cne '/' -or
            $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.IdnHost -ieq 'management.azure.com' -or
            -not $origins.Add($uri.AbsoluteUri) -or $model.entities -isnot [array] -or $model.entities.Count -eq 0) { throw 'Invalid model origin or entities.' }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entity in $model.entities) {
            if ($CloudHealthDiagnostics -and ($entity -isnot [Collections.IDictionary] -or
                $entity.healthState -cnotin @('Healthy', 'Degraded', 'Unhealthy', 'Unknown'))) { throw 'Invalid expected entity state.' }
            if ($entity.name -cnotmatch '^[a-zA-Z0-9](?:[a-zA-Z0-9-]{1,258})[a-zA-Z0-9]\z' -or -not $names.Add($entity.name) -or
                $entity.signals -isnot [array]) { throw 'Invalid entity.' }
            $signalNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($signal in $entity.signals) {
                if ($CloudHealthDiagnostics -and ($signal -isnot [Collections.IDictionary] -or
                    ($signal.Contains('value') -and -not (Test-ReadbackNumber $signal.value)))) { throw 'Invalid expected signal value.' }
                if ($signal.name -cnotmatch '^[a-zA-Z0-9](?:[a-zA-Z0-9-]{1,258})[a-zA-Z0-9]\z' -or
                    -not $signalNames.Add($signal.name) -or $signal.healthState -cnotin @('Healthy', 'Degraded', 'Unhealthy', 'Unknown')) { throw 'Invalid expected signal.' }
                if (-not $CloudHealthDiagnostics -and
                    (($signal.expiresInMinutes -isnot [long] -and $signal.expiresInMinutes -isnot [int]) -or
                    $signal.expiresInMinutes -lt 1 -or $signal.expiresInMinutes -gt 10080)) { throw 'Invalid expected signal TTL.' }
                $signalCount++
            }
            if ($entity.signals.Count -eq 0) { $emptyEntityCount++ }
            $entityCount++
        }
    }
    $phase = 'explicit credential identity'
    foreach ($audience in @('https://management.azure.com', 'https://data.cloudhealth.azure.com')) {
        $tokens[$audience] = Get-ReadbackToken $audience $subscription
    }
    $phase = 'ACI metadata request'
    $arm = 'https://management.azure.com'
    $group = Get-ReadbackJson "$arm$($expected.containerGroupId)?api-version=2025-09-01" $arm
    $phase = 'ACI image, identity and endpoint metadata'
    $properties = $group.properties
    $containers = @($properties.containers)
    $pull = @($properties.imageRegistryCredentials)
    if ($properties.osType -cne 'Linux' -or $properties.sku -cne 'Standard' -or $properties.restartPolicy -cne 'Always' -or
        $containers.Count -ne 1 -or $containers[0].properties.image -cne $expected.imageReference -or
        $containers[0].properties.instanceView.currentState.state -cne 'Running' -or
        $containers[0].properties.ports -or
        $group.identity.type -cne 'UserAssigned' -or -not $group.identity.userAssignedIdentities.Contains($expected.identityResourceId) -or
        $pull.Count -ne 1 -or $pull[0].identity -ine $expected.identityResourceId -or $pull[0].username -or $pull[0].password -or
        $pull[0].server -cne $expected.imageReference.Split('/')[0] -or
        ($properties.ipAddress -and ($properties.ipAddress.type -cne 'Private' -or $properties.ipAddress.ports)) -or
        $group.tags['she-network-mode'] -cne $expected.networkMode) { throw 'Unexpected container metadata.' }
    $subnets = @($properties.subnetIds | Where-Object { $null -ne $_ })
    if ($expected.networkMode -ceq 'public') {
        if ($subnets.Count -ne 0 -or $properties.ipAddress) { throw 'Public-egress mode must expose no IP or ports.' }
    }
    else {
        if ($subnets.Count -ne 1 -or $subnets[0].id -ine $expected.subnetId) { throw 'Unexpected ACI subnet.' }
        $subnet = Get-ReadbackJson "$arm$($expected.subnetId)?api-version=2024-05-01" $arm
        if ($subnet.properties.natGateway.id -ine $expected.natGatewayId -or
            @($subnet.properties.delegations | Where-Object { $_.properties.serviceName -ceq 'Microsoft.ContainerInstance/containerGroups' }).Count -ne 1) { throw 'Unexpected delegation or NAT association.' }
    }
    $phase = 'registry and vault access metadata'
    $registry = Get-ReadbackJson "$arm$($expected.registryResourceId)?api-version=2025-04-01" $arm
    $vault = Get-ReadbackJson "$arm$($expected.vaultResourceId)?api-version=2024-11-01" $arm
    if ($registry.properties.adminUserEnabled -ne $false -or $vault.properties.enableRbacAuthorization -ne $true) { throw 'Unexpected local authentication configuration.' }
    if ($registry.properties.publicNetworkAccess -cne $defaultPublicNetworkAccess -or
        $vault.properties.publicNetworkAccess -cne $vaultPublicNetworkAccess) { throw 'Unexpected public network access.' }
    foreach ($resource in @($registry, $vault)) {
        if ($expected.networkMode -ceq 'private' -and
            @($resource.properties.privateEndpointConnections | Where-Object {
                $_.properties.privateLinkServiceConnectionState.status -ceq 'Approved'
            }).Count -lt 1) { throw 'Missing approved private endpoint.' }
    }
    $phase = if ($CloudHealthDiagnostics) { 'CloudHealth diagnostics and ARM status readback' } else { 'exact model snapshot readback' }
    do {
        $matched = $true
        foreach ($model in $expected.models) {
            foreach ($entity in $model.entities) {
                $apiVersion = if ($CloudHealthDiagnostics) { $model.apiVersion } else { '2026-06-01-preview' }
                $url = $model.dataPlaneUri.TrimEnd('/') + '/api/entities/' + [uri]::EscapeDataString($entity.name) + '?api-version=' + $apiVersion
                $snapshot = Get-ReadbackJson $url 'https://data.cloudhealth.azure.com'
                if ($CloudHealthDiagnostics) {
                    $diagnosticsMatched = Test-CloudHealthEntity $snapshot $entity $model
                    $entityUrl = "$arm$($model.resourceId)/entities/$([uri]::EscapeDataString($entity.name))?api-version=$($model.controlPlaneApiVersion)"
                    $armSnapshot = Get-ReadbackJson $entityUrl $arm
                    $armMatched = Test-CloudHealthEntity $armSnapshot $entity $model -ArmStatus
                    if (-not $diagnosticsMatched -or -not $armMatched) { $matched = $false }
                }
                elseif (-not (Test-EntitySnapshot $snapshot $entity $mapping)) { $matched = $false }
            }
        }
        $null = Get-ReadbackTimeBudget
        if ($matched) { break }
        $remaining = Get-ReadbackTimeBudget
        if ($PollSeconds) { Start-Sleep -Seconds ([math]::Min($PollSeconds, $remaining)) }
    } while ($true)
    if ($CloudHealthDiagnostics) {
        Write-Output "Verified $entityCount entities and $signalCount signals: exact diagnostic and ARM names, states and required values matched."
        if ($signalCount) { Write-Output "ARM observed report freshness verified for $signalCount signals since the supplied NotBefore." }
        if ($emptyEntityCount) { Write-Output "ARM observed report freshness: not established for $emptyEntityCount empty-signal entities." }
        Write-Output 'Metadata verified: image/identity/network configuration matched.'
        Write-Output 'Exact TTL/expiration is not verified. Neither reviewed view supplies service expiration evidence.'
    }
    else { Write-Output "Verified $entityCount entities and $signalCount signals: exact names, states, TTLs and rollout freshness; image/identity/network metadata matched." }
    Write-Output 'This read-only check does not prove DNS reachability from ACI, RBAC propagation, future pulls or a liveness restart.'
}
catch { Write-Error "Verification failed ($phase). No payload, credential or native authentication error is emitted." -ErrorAction Continue; exit 1 }
finally { $tokens.Clear(); $clock.Stop() }
