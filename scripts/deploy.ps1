#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium', DefaultParameterSetName = 'File')]
param(
    [Parameter(Mandatory)][guid] $SubscriptionId,
    [Parameter(Mandatory, ParameterSetName = 'File')][string] $ParametersFile,
    # Keep arbitrary configuration behind the redacted error boundary, not the PowerShell binder.
    [Parameter(Mandatory, ParameterSetName = 'Objects')][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $Parameters,
    [string[]] $PackageDirectories,
    [string] $ImageReference,
    [switch] $PrivateNetworkReachable,
    [switch] $ConfirmRecreate,
    [string] $ManifestPath,
    [string] $BicepPath = $env:BICEP_CLI_PATH,
    [ValidateRange(1, 32768)][int] $MaxEnvironmentBytes = 32768,
    [ValidateRange(1, 20)][int] $RbacAttempts = 6,
    [ValidateRange(1, 120)][int] $ReadinessAttempts = 30,
    [ValidateRange(0, 60)][int] $RetryDelaySeconds = 15
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
. (Join-Path $PSScriptRoot 'environment.ps1')
. (Join-Path $PSScriptRoot 'deployment-ownership.ps1')
$work = $null
$phase = 'input'

function Assert-NonSecretConfiguration {
    [CmdletBinding()]
    param([AllowNull()][object] $Value, [string] $Name = '')
    if ($Value -is [Collections.IDictionary]) {
        if ($Value.type -ieq 'apiKey' -and $Value.Contains('headers')) {
            if ($Value.headers -isnot [Collections.IDictionary]) { throw 'Invalid credential headers.' }
            foreach ($secret in $Value.headers.Values) { Assert-NonSecretConfiguration -Value $secret -Name 'secret' }
        }
        foreach ($key in $Value.Keys) { Assert-NonSecretConfiguration -Value $Value[$key] -Name $key }
    }
    elseif ($Value -is [Collections.IEnumerable] -and $Value -isnot [string]) {
        foreach ($item in $Value) { Assert-NonSecretConfiguration -Value $item }
    }
    elseif ($Name -match '^(token|password|username|clientSecret|secret|certificate)$') {
        if ($Value -isnot [string] -or $Value -cnotmatch '^kv://[a-zA-Z0-9-]+/[a-zA-Z0-9-]+(?:/[a-zA-Z0-9-]+)?\z') {
            throw 'Credential fields must contain Key Vault references.'
        }
    }
    elseif ($Name -match '^(DD-API-KEY|DD-APPLICATION-KEY|X-SF-Token|Authorization)$') {
        if ($Value -isnot [string] -or $Value -cnotmatch '^kv://[a-zA-Z0-9-]+/[a-zA-Z0-9-]+(?:/[a-zA-Z0-9-]+)?\z') {
            throw 'Credential headers must contain Key Vault references.'
        }
    }
}

function Get-IPv4Network {
    [CmdletBinding()]
    param([string] $Prefix, [int] $MaxPrefix = 29)
    if ($Prefix -cnotmatch '^([0-9]{1,3}\.){3}[0-9]{1,3}/([0-9]{1,2})\z') { throw 'Invalid subnet.' }
    $bits = [int]($Prefix.Split('/')[1])
    if ($bits -lt 8 -or $bits -gt $MaxPrefix) { throw 'Invalid subnet size.' }
    $bytes = [Net.IPAddress]::Parse($Prefix.Split('/')[0]).GetAddressBytes()
    $number = [uint64]$bytes[0] * 16777216 + [uint64]$bytes[1] * 65536 + [uint64]$bytes[2] * 256 + $bytes[3]
    $size = [uint64][math]::Pow(2, 32 - $bits)
    if ($number % $size -ne 0) { throw 'Subnet must be aligned.' }
    return @{ Start = $number; End = $number + $size - 1 }
}

function Write-DeploymentParameter {
    [CmdletBinding()]
    param([Collections.IDictionary] $Desired, [string] $Path, [string] $TemplatePath)
    $values = @{}
    foreach ($key in $Desired.Keys) { $values[$key] = @{ value = $Desired[$key] } }
    $json = @{ '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#'
        contentVersion = '1.0.0.0'; parameters = $values } | ConvertTo-Json -Depth 64 -Compress -WarningAction Stop
    # Leave headroom below ARM's 4 MiB total request / parameter and 1 MiB resource limits.
    if ([Text.Encoding]::UTF8.GetByteCount($json) + (Get-Item -LiteralPath $TemplatePath).Length -gt 3MB) {
        throw 'ARM deployment request size budget exceeded.'
    }
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-GroupState {
    [CmdletBinding()]
    param([string] $Id)
    $json = Invoke-EvaluatorCommand az @('rest', '--method', 'get', '--url', "https://management.azure.com${Id}?api-version=2025-09-01",
        '--subscription', $SubscriptionId.ToString(), '--only-show-errors', '--output', 'json') 'container state inspection' -TimeoutSeconds 60
    return ConvertFrom-Json -InputObject $json -AsHashtable -Depth 64
}

function Test-GroupRecreation {
    [CmdletBinding()]
    param([Collections.IDictionary] $Group, [Collections.IDictionary] $Desired, [string] $SubnetId)
    $properties = $Group.properties
    if ($Group.location.Replace(' ', '') -ine $Desired.location -or $properties.osType -cne 'Linux' -or
        $properties.restartPolicy -cne 'Always' -or $properties.sku -cne 'Standard' -or @($properties.containers).Count -ne 1) { return $true }
    $requests = $properties.containers[0].properties.resources.requests
    if ($requests.cpu -ne $Desired.cpu -or $requests.memoryInGB -ne $Desired.memoryInGB) { return $true }
    if (((@($Group.zones) | Sort-Object) -join ',') -cne ((@($Desired.zones) | Sort-Object) -join ',')) { return $true }
    $subnets = @($properties.subnetIds | ForEach-Object { $_.id })
    if (($subnets -join ',') -ine $SubnetId) { return $true }
    if ($Group.tags -and $Group.tags.Contains('she-network-mode') -and $Group.tags['she-network-mode'] -cne $Desired.networkMode) { return $true }
    return $false
}

try {
    if ($PSCmdlet.ParameterSetName -eq 'File') {
        $parameterSourceJson = Get-Content -LiteralPath $ParametersFile -Raw
        $inputObject = ConvertFrom-EvaluatorConfigurationJson $parameterSourceJson
        if ($inputObject -isnot [Collections.IDictionary] -or $inputObject.parameters -isnot [Collections.IDictionary]) { throw 'Expected ARM parameters JSON.' }
        $Parameters = @{}
        foreach ($key in $inputObject.parameters.Keys) {
            $entry = $inputObject.parameters[$key]
            if ($entry -isnot [Collections.IDictionary] -or $entry.Count -ne 1 -or -not $entry.Contains('value')) { throw 'Expected non-secret inline parameters.' }
            $Parameters[$key] = $entry.value
        }
    }
    if ($Parameters -isnot [Collections.IDictionary]) { throw 'Expected desired-state parameters dictionary.' }
    # Snapshot caller-owned objects before any external tool can mutate them.
    $objectSourceJson = ConvertTo-Json -InputObject $Parameters -Depth 64 -Compress -WarningAction Stop
    $desired = ConvertFrom-EvaluatorConfigurationJson $objectSourceJson
    $null = ConvertTo-EvaluatorEnvironment -Plugins $desired.pluginConfigurations -Models $desired.modelConfigurations `
        -HostSettings $(if ($desired.Contains('hostSettings')) { $desired.hostSettings } else { @{} }) -MaxEnvironmentBytes $MaxEnvironmentBytes
    Assert-NonSecretConfiguration $desired
    $parameterRevision = if ($PSCmdlet.ParameterSetName -eq 'File') {
        Get-EvaluatorParameterRevision -Json $parameterSourceJson -ArmParameterFile
    }
    else { Get-EvaluatorParameterRevision -Json $objectSourceJson }
    if ($ImageReference -and $ImageReference -cnotmatch '^[a-z0-9]+\.azurecr\.io/[a-z0-9]+(?:[._/-][a-z0-9]+)*@sha256:[a-f0-9]{64}\z') { throw 'A pushed registry manifest reference is required.' }
    if (-not $ImageReference -and @($PackageDirectories).Count -eq 0) { throw 'Select published package directories or an existing registry manifest reference.' }
    if ($ImageReference -and $PackageDirectories) { throw 'Choose build/push or reuse of a manifest, not both.' }
    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess('Evaluator deployment', 'Run Azure what-if, build/push, validate and deploy complete desired state')
        return
    }
    $work = New-EvaluatorWorkDirectory
    $template = Join-Path $work 'main.json'
    if (-not $BicepPath) { $BicepPath = 'bicep' }
    $null = Invoke-EvaluatorCommand $BicepPath @('build', (Join-Path $PSScriptRoot '..\infra\main.bicep'), '--outfile', $template) 'Bicep compilation'
    $schema = ConvertFrom-EvaluatorConfigurationJson (Get-Content -LiteralPath $template -Raw)
    foreach ($key in @($desired.Keys)) {
        if ($key -cnotin @($schema.parameters.Keys)) { throw 'Unknown deployment parameter.' }
    }
    foreach ($key in $schema.parameters.Keys) {
        if (-not $desired.Contains($key)) {
            if (-not $schema.parameters[$key].Contains('defaultValue')) { throw 'Missing required deployment parameter.' }
            $desired[$key] = $schema.parameters[$key].defaultValue
        }
    }
    if ($desired.namePrefix -cnotmatch '^[a-z][a-z0-9]{2,9}\z' -or $desired.resourceGroupName -cnotmatch '^[a-zA-Z0-9_-]{1,90}\z' -or
        $desired.imageRepository -cnotmatch '^[a-z0-9]+(?:[._/-][a-z0-9]+)*\z') { throw 'Invalid resource or repository name.' }
    if ($desired.location -cnotmatch '^[a-z]+[0-9]*\z' -or $desired.location -cnotin @(
            'eastus', 'eastus2', 'westus2', 'westus3', 'centralus', 'northeurope', 'westeurope', 'uksouth',
            'australiaeast', 'southeastasia', 'japaneast')) { throw 'Region is outside this design public-cloud compatibility allow-list.' }
    if ($desired.networkMode -cnotin @('public', 'vnetNat', 'private')) { throw 'Invalid network mode.' }
    if ($desired.keyVaultPublicNetworkAccess -isnot [string] -or
        $desired.keyVaultPublicNetworkAccess -cnotin @('Enabled', 'Disabled')) { throw 'Invalid Key Vault public network access.' }
    if ($desired.networkMode -ceq 'private' -and -not $PrivateNetworkReachable) { throw 'Private mode requires explicit operator private-network/DNS reachability.' }
    foreach ($key in @('vnetAddressPrefix', 'aciSubnetPrefix', 'endpointSubnetPrefix')) {
        if ($desired[$key] -isnot [string] -or $desired[$key].Length -gt 18) { throw 'Invalid network prefix.' }
    }
    $vnet = Get-IPv4Network $desired.vnetAddressPrefix
    $aci = Get-IPv4Network $desired.aciSubnetPrefix -MaxPrefix 24
    $endpoint = Get-IPv4Network $desired.endpointSubnetPrefix
    if ($desired.networkMode -cne 'public') {
        if ($aci.Start -lt $vnet.Start -or $aci.End -gt $vnet.End -or $endpoint.Start -lt $vnet.Start -or $endpoint.End -gt $vnet.End -or
            ($aci.Start -le $endpoint.End -and $endpoint.Start -le $aci.End)) { throw 'Subnets must be separate and contained by the VNet.' }
    }
    foreach ($key in @('cpu', 'memoryInGB', 'livenessInitialDelaySeconds', 'readinessInitialDelaySeconds', 'probePeriodSeconds', 'probeTimeoutSeconds', 'probeFailureThreshold')) {
        if ($desired[$key] -isnot [long] -and $desired[$key] -isnot [int]) { throw 'Compute/probe values must be integers.' }
        if ($desired[$key] -lt 1 -or $desired[$key] -gt 3600) { throw 'Compute/probe value out of range.' }
    }
    if ($desired.cpu -gt 31 -or $desired.memoryInGB -gt 240 -or $desired.probeTimeoutSeconds -lt 3) { throw 'Compute/probe value out of range.' }
    if ($desired.zones -isnot [array] -or $desired.zones.Count -gt 1 -or @($desired.zones | Where-Object { $_ -cnotin @('1', '2', '3') }).Count) { throw 'Invalid availability zone selection.' }
    if ($desired.tags -isnot [Collections.IDictionary] -or $desired.healthModelResourceIds -isnot [array] -or $desired.healthModelResourceIds.Count -lt 1 -or
        $desired.adminPrincipalIds -isnot [array]) { throw 'Invalid desired state.' }
    if ($desired.tags.Count -gt 48 -or $desired.imageRepository.Length -gt 200) { throw 'ARM tags or repository limits exceeded.' }
    foreach ($key in $desired.tags.Keys) {
        if ($key -isnot [string] -or $key.Length -lt 1 -or $key.Length -gt 512 -or $desired.tags[$key] -isnot [string] -or $desired.tags[$key].Length -gt 256 -or
            $key -iin @('she-network-mode', 'she-owner')) { throw 'Invalid resource tags.' }
    }
    $modelIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $desired.healthModelResourceIds) {
        if ($id -isnot [string] -or $id -inotmatch '^/subscriptions/[a-f0-9-]{36}/resourceGroups/[a-z0-9_.()-]{1,90}/providers/Microsoft\.CloudHealth/healthmodels/[a-zA-Z][a-zA-Z0-9-]{1,42}[a-zA-Z0-9]\z' -or
            $id.Contains('00000000-0000-0000-0000-000000000000') -or -not $modelIds.Add($id)) { throw 'Invalid, duplicate or example model ID.' }
        $parsed = [guid]::Empty
        if (-not [guid]::TryParse($id.Split('/')[2], [ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'Invalid model subscription ID.' }
    }
    $adminIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($id in $desired.adminPrincipalIds) {
        $parsed = [guid]::Empty
        if ($id -isnot [string] -or -not [guid]::TryParse($id, [ref]$parsed) -or $parsed -eq [guid]::Empty -or -not $adminIds.Add($id)) { throw 'Invalid or duplicate administrator principal ID.' }
    }
    if (-not $ManifestPath) {
        $ManifestPath = Join-Path $PSScriptRoot "..\artifacts\deployments\$SubscriptionId-$($desired.resourceGroupName)-$($desired.namePrefix).json"
    }
    $manifestExists = [IO.File]::Exists($ManifestPath)
    $manifest = if ($manifestExists) { Read-EvaluatorManifest $ManifestPath } else {
        @{
            schemaVersion = 1; subscriptionId = $SubscriptionId.ToString(); resourceGroupName = $desired.resourceGroupName
            namePrefix = $desired.namePrefix; owner = [guid]::NewGuid().ToString(); resourceGroupCreated = $false
            healthModelResourceIds = @(); resources = @()
        }
    }
    if ($manifest.subscriptionId -ine $SubscriptionId.ToString() -or $manifest.resourceGroupName -ine $desired.resourceGroupName -or
        $manifest.namePrefix -cne $desired.namePrefix) { throw 'Ownership manifest belongs to a different rollout.' }
    $manifest.healthModelResourceIds = @(@($manifest.healthModelResourceIds) + @($desired.healthModelResourceIds) | Sort-Object -Unique)
    $desired.tags['she-owner'] = $manifest.owner
    $desired.deployContainerGroup = $false
    $desired.imageDigest = ''
    $parameterFile = Join-Path $work 'desired.parameters.json'
    if ($desired.healthModelResourceIds.Count + $desired.adminPrincipalIds.Count -gt 500) { throw 'ARM resource count budget exceeded.' }
    Write-DeploymentParameter $desired $parameterFile $template
    $phase = 'public-cloud and region preflight'
    $cloud = Invoke-EvaluatorCommand az @('cloud', 'show', '--output', 'json', '--only-show-errors') $phase | ConvertFrom-Json
    $account = Invoke-EvaluatorCommand az @('account', 'show', '--subscription', $SubscriptionId.ToString(), '--output', 'json', '--only-show-errors') $phase | ConvertFrom-Json
    if ($cloud.name -cne 'AzureCloud' -or $account.environmentName -cne 'AzureCloud' -or $account.id -ine $SubscriptionId.ToString()) { throw 'Azure public cloud and explicit subscription required.' }
    $regions = Invoke-EvaluatorCommand az @('provider', 'show', '--namespace', 'Microsoft.ContainerInstance', '--subscription', $SubscriptionId.ToString(),
        '--query', "resourceTypes[?resourceType=='containerGroups'].locations | [0]", '--output', 'json', '--only-show-errors') $phase | ConvertFrom-Json
    $supported = @($regions | ForEach-Object { $_.Replace(' ', '').ToLowerInvariant() })
    if ($desired.location -cnotin $supported) { throw 'ACI is unavailable in the requested subscription/region.' }
    $groupName = $desired.namePrefix + '-evaluator'
    $groupId = "/subscriptions/$SubscriptionId/resourceGroups/$($desired.resourceGroupName)/providers/Microsoft.ContainerInstance/containerGroups/$groupName"
    $phase = 'what-if'
    $deploymentArgs = @('deployment', 'sub', 'what-if', '--subscription', $SubscriptionId.ToString(), '--location', $desired.location,
        '--template-file', $template, '--parameters', "@$parameterFile", '--result-format', 'ResourceIdOnly', '--no-pretty-print', '--only-show-errors', '--output', 'json')
    $preview = Invoke-EvaluatorCommand az $deploymentArgs $phase | ConvertFrom-Json -AsHashtable -Depth 64
    if (-not $PSCmdlet.ShouldProcess($desired.resourceGroupName, 'Deploy Bicep prerequisites (no container group), then build/push and validate image')) { return }
    $phase = 'ownership preflight and manifest persistence'
    if (-not $manifestExists) {
        $exists = Invoke-EvaluatorAzJson @('group', 'exists', '--name', $desired.resourceGroupName) $manifest.subscriptionId $phase
        if ($exists -isnot [bool]) { throw 'Invalid resource group existence response.' }
        $manifest.resourceGroupCreated = -not $exists
    }
    Add-EvaluatorDeploymentPlan $manifest $desired $preview 'prerequisites'
    $inventory = @(Get-EvaluatorGroupInventory $manifest)
    $ownedLive = @(Get-EvaluatorOwnedLiveResource $manifest $inventory)
    Assert-EvaluatorLiveOwnership $manifest $ownedLive
    Write-EvaluatorManifest $ManifestPath $manifest
    Write-Output "Ownership manifest: $([IO.Path]::GetFullPath($ManifestPath))"
    $phase = 'prerequisites'
    $deploymentArgs = @('deployment', 'sub', 'create', '--subscription', $SubscriptionId.ToString(), '--location', $desired.location,
        '--name', "$($desired.namePrefix)-prerequisites", '--template-file', $template, '--parameters', "@$parameterFile", '--only-show-errors', '--output', 'json')
    $prerequisites = Invoke-EvaluatorCommand az $deploymentArgs $phase | ConvertFrom-Json -AsHashtable -Depth 64
    if ($prerequisites.properties.provisioningState -cne 'Succeeded') { throw 'Prerequisite deployment did not succeed.' }
    $outputs = $prerequisites.properties.outputs
    $manifest.identityPrincipalId = $outputs.identityPrincipalId.value
    Write-EvaluatorManifest $ManifestPath $manifest
    $registry = $outputs.registryLoginServer.value
    if ($registry -cnotmatch '^[a-z0-9]+\.azurecr\.io\z') { throw 'Invalid registry output.' }
    $registryName = $registry.Split('.')[0]
    $registryId = "/subscriptions/$SubscriptionId/resourceGroups/$($desired.resourceGroupName)/providers/Microsoft.ContainerRegistry/registries/$registryName"
    $phase = 'image build or manifest verification'
    $null = Invoke-EvaluatorCommand az @('acr', 'login', '--name', $registryName, '--subscription', $SubscriptionId.ToString(), '--only-show-errors', '--output', 'none') 'registry login'
    if (-not $ImageReference) {
        $image = & (Join-Path $PSScriptRoot 'build-image.ps1') -PackageDirectories $PackageDirectories -Push -Registry $registry `
            -Repository $desired.imageRepository -Platform 'linux/amd64'
        if ($LASTEXITCODE -ne 0 -or $image.DigestKind -cne 'RegistryManifest' -or $image.Platform -cne 'linux/amd64') { throw 'A pushed amd64 registry manifest is required.' }
        $ImageReference = $image.ImageReference
        if ($ImageReference -cne "$registry/$($desired.imageRepository)@$($image.Digest)") { throw 'Build reference mismatch.' }
    }
    Assert-EvaluatorImageReference $ImageReference
    $digest = $ImageReference.Split('@')[-1]
    if ($ImageReference -cne "$registry/$($desired.imageRepository)@$digest") { throw 'Manifest must belong to the desired registry/repository.' }
    $registryManifest = Invoke-EvaluatorCommand docker @('buildx', 'imagetools', 'inspect', $ImageReference, '--format', '{{json .Manifest}}') 'registry manifest verification' | ConvertFrom-Json
    if ($registryManifest.digest -cne $digest) { throw 'Registry manifest digest mismatch.' }
    $null = Invoke-EvaluatorCommand docker @('pull', '--platform', 'linux/amd64', $ImageReference) 'exact deployment image pull'
    $platform = Invoke-EvaluatorCommand docker @('image', 'inspect', '--format', '{{.Os}}/{{.Architecture}}', $ImageReference) 'ACI image architecture inspection'
    if ($platform -cne 'linux/amd64') { throw 'ACI requires a Linux amd64 image.' }
    $phase = 'exact-image configuration validation'
    $insightsId = "/subscriptions/$SubscriptionId/resourceGroups/$($desired.resourceGroupName)/providers/Microsoft.Insights/components/$($desired.namePrefix)-insights"
    $connection = Invoke-EvaluatorCommand az @('resource', 'show', '--ids', $insightsId, '--api-version', '2020-02-02', '--query', 'properties.ConnectionString',
        '--subscription', $SubscriptionId.ToString(), '--output', 'tsv', '--only-show-errors') 'telemetry configuration'
    $generated = @{ AZURE_CLIENT_ID = $outputs.identityClientId.value; APPLICATIONINSIGHTS_CONNECTION_STRING = $connection; SHE_PACKAGES_DIR = '/app/packages' }
    $environment = ConvertTo-EvaluatorEnvironment -Plugins $desired.pluginConfigurations -Models $desired.modelConfigurations -HostSettings $desired.hostSettings `
        -GeneratedEnvironment $generated -MaxEnvironmentBytes $MaxEnvironmentBytes
    if ($environment.Count -lt 5) { throw 'Incomplete generated environment.' }
    $manifest.localContainerCleanup = $true
    Write-EvaluatorManifest $ManifestPath $manifest
    $null = & (Join-Path $PSScriptRoot 'validate-config.ps1') -ImageReference $ImageReference -Plugins $desired.pluginConfigurations `
        -Models $desired.modelConfigurations -HostSettings $desired.hostSettings -GeneratedEnvironment $generated -MaxEnvironmentBytes $MaxEnvironmentBytes `
        -RolloutOwner $manifest.owner
    if ($LASTEXITCODE -ne 0) { throw 'Image configuration rejected.' }
    $desired.deployContainerGroup = $true
    $desired.imageDigest = $digest
    Write-DeploymentParameter $desired $parameterFile $template
    $preview = Invoke-EvaluatorCommand az @('deployment', 'sub', 'what-if', '--subscription', $SubscriptionId.ToString(), '--location', $desired.location,
        '--template-file', $template, '--parameters', "@$parameterFile", '--result-format', 'ResourceIdOnly', '--no-pretty-print', '--only-show-errors', '--output', 'json') 'validated full-state what-if' |
        ConvertFrom-Json -AsHashtable -Depth 64
    $phase = 'full-state ownership preflight and manifest persistence'
    Add-EvaluatorDeploymentPlan $manifest $desired $preview 'container'
    $inventory = @(Get-EvaluatorGroupInventory $manifest)
    $ownedLive = @(Get-EvaluatorOwnedLiveResource $manifest $inventory)
    Assert-EvaluatorLiveOwnership $manifest $ownedLive
    Write-EvaluatorManifest $ManifestPath $manifest
    $phase = 'AcrPull propagation'
    $pullRole = '/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d'
    $assigned = $false
    for ($attempt = 0; $attempt -lt $RbacAttempts; $attempt++) {
        try {
            $roles = Invoke-EvaluatorCommand az @('role', 'assignment', 'list', '--scope', $registryId, '--fill-principal-name', 'false',
                '--fill-role-definition-name', 'false', '--subscription', $SubscriptionId.ToString(), '--only-show-errors', '--output', 'json') $phase -TimeoutSeconds 60 | ConvertFrom-Json -NoEnumerate
            if ($roles -isnot [array]) { throw 'Expected a role assignment array.' }
        }
        catch {
            $phase = 'AcrPull lookup failed; verify Azure CLI syntax, authentication and registry role-assignment read permission'
            throw
        }
        if (@($roles | Where-Object { $_.principalId -ieq $outputs.identityPrincipalId.value -and $_.scope -ieq $registryId -and $_.roleDefinitionId.EndsWith($pullRole, [StringComparison]::OrdinalIgnoreCase) }).Count) {
            $assigned = $true
            Start-Sleep -Seconds $RetryDelaySeconds
            break
        }
        if ($attempt + 1 -lt $RbacAttempts) { Start-Sleep -Seconds $RetryDelaySeconds }
    }
    if (-not $assigned) {
        $phase = 'AcrPull assignment not visible within retry budget; verify the UAMI AcrPull assignment at the registry scope'
        throw 'AcrPull assignment was not visible within the retry budget.'
    }
    $phase = 'existing group compatibility inspection'
    $existing = Invoke-EvaluatorCommand az @('container', 'list', '--subscription', $SubscriptionId.ToString(), '--query', "[?id=='$groupId'].id",
        '--output', 'json', '--only-show-errors') $phase | ConvertFrom-Json
    if (@($existing).Count) {
        $current = Get-GroupState $groupId
        if ($current.tags['she-owner'] -cne $manifest.owner) { throw 'Existing evaluator group is not owned by this rollout.' }
        $subnetId = if ($desired.networkMode -ceq 'public') { '' } else {
            "/subscriptions/$SubscriptionId/resourceGroups/$($desired.resourceGroupName)/providers/Microsoft.Network/virtualNetworks/$($desired.namePrefix)-vnet/subnets/aci"
        }
        if (Test-GroupRecreation $current $desired $subnetId) {
            if (-not $ConfirmRecreate) { $phase = 'explicit recreation confirmation required; rerun with -ConfirmRecreate after reviewing downtime'; throw 'Recreation required.' }
            if (-not $PSCmdlet.ShouldProcess($groupId, 'DELETE existing container group and recreate it; reporting and in-memory work will be interrupted')) { return }
            $null = Invoke-EvaluatorCommand az @('container', 'delete', '--name', $groupName, '--resource-group', $desired.resourceGroupName, '--yes',
                '--subscription', $SubscriptionId.ToString(), '--only-show-errors', '--output', 'none') 'explicit container recreation'
        }
    }
    if (-not $PSCmdlet.ShouldProcess($groupId, 'Deploy full validated desired container state')) { return }
    $phase = 'container deployment'
    $deploymentArgs[[Array]::IndexOf($deploymentArgs, '--name') + 1] = "$($desired.namePrefix)-container"
    $deployed = $false
    for ($attempt = 0; $attempt -lt $RbacAttempts; $attempt++) {
        try {
            $result = Invoke-EvaluatorCommand az $deploymentArgs $phase | ConvertFrom-Json -AsHashtable -Depth 64
            if ($result.properties.provisioningState -cne 'Succeeded') { throw 'Container deployment did not succeed.' }
            $deployed = $true
            break
        }
        catch {
            $imagePullFailure = [bool]$_.Exception.Data['ImagePullFailure']
            if (-not $imagePullFailure) {
                $state = Get-GroupState $groupId
                $events = @($state.properties.instanceView.events) + @($state.properties.containers | ForEach-Object { $_.properties.instanceView.events })
                $imagePullFailure = @($events | Where-Object { $_.name -cin @('InaccessibleImage', 'RegistryErrorResponse', 'FailedToPullImage') }).Count -gt 0
            }
            if ($attempt + 1 -ge $RbacAttempts -or -not $imagePullFailure) { throw 'Container deployment failed; inspect sanitized event codes and ARM deployment status.' }
            Start-Sleep -Seconds $RetryDelaySeconds
        }
    }
    if (-not $deployed) { throw 'Container deployment retry budget exhausted.' }
    $phase = 'readiness and lifecycle inspection'
    $ready = $false
    for ($attempt = 0; $attempt -lt $ReadinessAttempts; $attempt++) {
        $state = Get-GroupState $groupId
        $events = @($state.properties.instanceView.events) + @($state.properties.containers | ForEach-Object { $_.properties.instanceView.events })
        Write-Verbose "Inspected $($events.Count) lifecycle events; event messages are not logged."
        if ($state.properties.provisioningState -ceq 'Failed') { throw 'Container provisioning failed.' }
        $container = $state.properties.containers[0]
        $containerView = $container.properties.instanceView
        if ($containerView.restartCount -gt 0) { throw "Evaluator container has restarted ($($containerView.restartCount) restarts). Inspect ACI state/events." }
        $containerState = $containerView.currentState.state
        $currentEvents = @($containerView.events | Where-Object {
            -not $_.lastTimestamp -or -not $containerView.currentState.startTime -or
            ([DateTimeOffset]$_.lastTimestamp).ToUnixTimeSeconds() -ge ([DateTimeOffset]$containerView.currentState.startTime).ToUnixTimeSeconds()
        })
        # ACI HTTP probe failures are transient while the container is Running and has not restarted.
        $terminalWarnings = @($currentEvents | Where-Object {
            $_.type -eq 'Warning' -and $_.name -notin @('Pulling', 'BackOff', 'FailedToPullImage') -and
            -not ($containerState -ceq 'Running' -and $_.name -eq 'Failed' -and $_.message -cmatch '^(Readiness|Liveness) probe failed:')
        })
        if ($containerState -cnotin @('Running', 'Waiting', 'Pending') -or $terminalWarnings.Count) {
            throw 'Evaluator container failed with an unrecognized/terminal state. Inspect ACI events and permissions.'
        }
        if ($state.properties.provisioningState -ceq 'Succeeded' -and $state.properties.instanceView.state -ceq 'Running' -and $containerState -ceq 'Running') { $ready = $true; break }
        if ($attempt + 1 -lt $ReadinessAttempts) { Start-Sleep -Seconds $RetryDelaySeconds }
    }
    if (-not $ready) { throw 'Timed out waiting for ARM Succeeded and the evaluator container Running.' }
    Write-Output "Deployment ready: $groupId image $ImageReference parameter-source sha256:$parameterRevision"
}
catch {
    # Never print native output, exception objects, parameters, config, telemetry strings or sink data.
    [Console]::Error.WriteLine("Deployment failed ($phase). No configuration payloads are logged. Review input or inspect Azure lifecycle event codes with an authorized operator.")
    exit 1
}
finally {
    if ($work -and [IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
