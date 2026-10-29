#Requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $SubscriptionId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $ResourceGroupName,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string] $Location,
    [string] $NamePrefix = 'shedemo',
    [ValidateRange(1, 3600)][int] $IntervalSeconds = 5,
    [ValidateNotNullOrEmpty()][string] $ManifestPath = (Join-Path $PSScriptRoot '..\artifacts\deployment.json'),
    [string] $NuGetSource,
    [string] $BicepPath = $env:BICEP_CLI_PATH
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
$phase = 'local input validation'
$manifest = $null

function Assert-DemoPreview {
    param([Collections.IDictionary] $Preview, [Collections.IDictionary] $Metadata, [switch] $Runtime)
    if ($Preview.status -cne 'Succeeded' -or $Preview.changes -isnot [array] -or $Preview.error) {
        throw 'Azure what-if must return a successful full-resource change list.'
    }
    if (-not $Runtime) {
        $registries = @($Preview.changes | Where-Object { $_.changeType -ne 'Ignore' -and $_.after.type -ieq 'Microsoft.ContainerRegistry/registries' })
        if ($registries.Count -ne 1 -or $registries[0].after.tags['she-demo-system'] -cnotmatch '^[a-z0-9]{13}\z') {
            throw 'Cannot resolve ownership from the actual foundation what-if.'
        }
        $Metadata.owner = $registries[0].after.tags['she-demo-system']
    }
    $targets = @(Get-DemoTarget $Metadata)
    $selected = @($targets | Where-Object { ($_.type -ieq 'Microsoft.ContainerInstance/containerGroups') -eq [bool]$Runtime })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($change in $Preview.changes) {
        if ($change.changeType -eq 'Ignore') { continue }
        if ($change.resourceId -isnot [string] -or -not $seen.Add($change.resourceId) -or $change.changeType -eq 'Delete') {
            throw 'Unexpected, duplicate or destructive change in Azure what-if.'
        }
        $target = @($selected | Where-Object { $_.id -ieq $change.resourceId })
        if ($target.Count -eq 1) {
            if ($change.after.type -ine $target[0].type -or $change.after.tags['she-demo-system'] -cne $Metadata.owner) {
                throw 'What-if target type/ownership does not match the exact demo naming boundary.'
            }
            continue
        }
        $allowedChild = $false
        if (-not $Runtime) {
            foreach ($parent in $targets) {
                $suffix = if ($parent.type -ieq 'Microsoft.CloudHealth/healthmodels') { '/(entities|relationships)/[a-z0-9-]+' }
                    elseif ($parent.type -iin @('Microsoft.ContainerRegistry/registries', 'Microsoft.Insights/dataCollectionRules')) {
                        '/providers/Microsoft.Authorization/roleAssignments/[a-f0-9-]{36}'
                    } else { $null }
                if ($suffix -and $change.resourceId -imatch ('^' + [regex]::Escape($parent.id) + $suffix + '\z')) { $allowedChild = $true }
            }
        }
        if (-not $allowedChild) { throw "What-if escaped the selected demo boundary: $($change.resourceId)." }
    }
    foreach ($target in $selected) {
        if (-not $seen.Contains($target.id)) { throw 'Azure what-if omitted an expected owned top-level target.' }
    }
}

try {
    Assert-DemoInput $SubscriptionId $ResourceGroupName $Location $NamePrefix
    if ($PSBoundParameters.ContainsKey('NuGetSource')) {
        $sourceUri = $null
        if ([string]::IsNullOrWhiteSpace($NuGetSource) -or
            -not [uri]::TryCreate($NuGetSource, [UriKind]::Absolute, [ref]$sourceUri) -or
            $sourceUri.Scheme -cne 'https' -or -not $sourceUri.Host -or $sourceUri.UserInfo -or $sourceUri.Query -or $sourceUri.Fragment -or
            $NuGetSource -match '[\s\\?#]|^https://[^/]*@') {
            throw 'NuGetSource must be an absolute HTTPS URI without credentials, query or fragment.'
        }
    }
    if ([string]::IsNullOrWhiteSpace($ManifestPath)) { throw 'ManifestPath must not be empty.' }
    $ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if ($WhatIfPreference) {
        $null = $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroupName ($Location, $NamePrefix)",
            'Independently deploy demo prerequisites, build/push image and deploy private runtime')
        return
    }
    if (-not $PSCmdlet.ShouldProcess("$SubscriptionId/$ResourceGroupName", 'Deploy only the independent demo system, with ownership preflight')) { return }
    $phase = 'Bicep compilation'
    if (-not $BicepPath) { $BicepPath = 'bicep' }
    $artifactDirectory = [IO.Path]::GetDirectoryName($ManifestPath)
    $null = [IO.Directory]::CreateDirectory($artifactDirectory)
    foreach ($name in @('main', 'runtime')) {
        $null = Invoke-EvaluatorCommand $BicepPath @('build', (Join-Path $PSScriptRoot "..\infra\$name.bicep"),
            '--no-restore', '--outfile', (Join-Path $artifactDirectory "$name.json")) $phase -TimeoutSeconds 120
    }
    $phase = 'public cloud and subscription validation'
    Assert-DemoCloud $SubscriptionId
    $phase = 'selected resource group'
    $exists = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz @('group', 'exists', '--name', $ResourceGroupName) $SubscriptionId $phase)
    if ($exists -isnot [bool]) { throw 'Invalid resource group existence response.' }
    if (-not $exists) {
        $null = Invoke-DemoAz @('group', 'create', '--name', $ResourceGroupName, '--location', $Location) $SubscriptionId $phase
    }
    $metadata = @{ schemaVersion = 1; subscriptionId = $SubscriptionId; resourceGroupName = $ResourceGroupName; location = $Location; namePrefix = $NamePrefix }
    $foundationArguments = @('--resource-group', $ResourceGroupName, '--name', "$NamePrefix-demo-prerequisites",
        '--template-file', (Join-Path $artifactDirectory 'main.json'), '--parameters', "location=$Location", "namePrefix=$NamePrefix")
    $phase = 'foundation ownership preflight'
    $preview = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz (@('deployment', 'group', 'what-if') + $foundationArguments +
        @('--result-format', 'FullResourcePayloads', '--no-pretty-print')) $SubscriptionId $phase -TimeoutSeconds 600)
    Write-DemoJson (Join-Path $artifactDirectory 'main.what-if.json') $preview
    Assert-DemoPreview $preview $metadata
    $live = @(Get-DemoLiveResource $SubscriptionId $ResourceGroupName)
    Assert-DemoOwnership @(Get-DemoTarget $metadata) $live $metadata.owner
    $phase = 'prerequisite deployment'
    $deployment = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz (@('deployment', 'group', 'create') + $foundationArguments) $SubscriptionId $phase -TimeoutSeconds 900)
    if ($deployment.properties.provisioningState -cne 'Succeeded') { throw 'Prerequisite ARM provisioningState is not Succeeded.' }
    $outputs = $deployment.properties.outputs
    $outputNames = @('owner', 'registryName', 'registryLoginServer', 'registryResourceId', 'identityName', 'identityResourceId',
        'identityClientId', 'identityPrincipalId', 'workspaceResourceId', 'workspaceQueryEndpoint', 'healthModelResourceId',
        'dataCollectionRuleResourceId', 'dataCollectionEndpointResourceId', 'metricsIngestionEndpoint', 'containerGroupName', 'containerGroupResourceId')
    if ($outputs -isnot [Collections.IDictionary] -or $outputs.Count -ne $outputNames.Count) { throw 'Unexpected foundation output contract.' }
    $candidate = $metadata.Clone()
    foreach ($name in $outputNames) {
        if ($outputs[$name].type -ine 'String' -or $outputs[$name].value -isnot [string] -or [string]::IsNullOrWhiteSpace($outputs[$name].value)) {
            throw "Missing or invalid foundation output: $name."
        }
        $candidate[$name] = $outputs[$name].value
    }
    if ($candidate.owner -cne $metadata.owner) { throw 'Deployment owner differs from ownership preflight.' }
    $candidate.resources = @(Get-DemoTarget $candidate | ForEach-Object { @{ id = $_.id; type = $_.type; apiVersion = $_.apiVersion } })
    $candidate.status = 'PrerequisitesSucceeded'
    Assert-DemoManifest $candidate
    $manifest = $candidate
    Write-DemoJson $ManifestPath $manifest
    $phase = 'registry login'
    $null = Invoke-DemoAz @('acr', 'login', '--name', $manifest.registryName) $SubscriptionId $phase
    $phase = 'image build and push'
    $temporaryTag = 'build-' + [guid]::NewGuid().ToString('N')
    $demoDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $buildArguments = @('buildx', 'build', '--platform', 'linux/amd64', '--push',
        '--file', (Join-Path $demoDirectory 'Dockerfile'), '--tag', "$($manifest.registryLoginServer)/checkout-demo:$temporaryTag")
    if ($NuGetSource) { $buildArguments += @('--build-arg', "NUGET_SOURCE=$NuGetSource") }
    $null = Invoke-EvaluatorCommand docker ($buildArguments + @($demoDirectory)) $phase -TimeoutSeconds 1800
    $phase = 'registry manifest verification'
    $image = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz @('acr', 'repository', 'show', '--name', $manifest.registryName,
        '--image', "checkout-demo:$temporaryTag") $SubscriptionId $phase)
    if ($image.digest -isnot [string] -or $image.digest -cnotmatch '^sha256:[a-f0-9]{64}\z') { throw 'Registry did not return a verified manifest digest.' }
    $manifest.appImage = "$($manifest.registryLoginServer)/checkout-demo@$($image.digest)"
    $manifest.status = 'ImagePushed'
    Write-DemoJson $ManifestPath $manifest
    $phase = 'scoped AcrPull visibility'
    $roleVisible = $false
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        $json = Invoke-DemoAz @('role', 'assignment', 'list', '--scope', $manifest.registryResourceId,
            '--fill-principal-name', 'false', '--fill-role-definition-name', 'false') $SubscriptionId $phase -TimeoutSeconds 60
        $roles = (ConvertFrom-EvaluatorConfigurationJson ('{"items":' + $json + '}')).items
        if ($roles -isnot [array]) { throw 'Invalid scoped role assignment response.' }
        foreach ($role in $roles) {
            if ($role.principalId -isnot [string] -or $role.scope -isnot [string] -or
                $role.roleDefinitionId -isnot [string]) { throw 'Unexpected principal/scope in scoped role assignment response.' }
            if ($role.principalId -ieq $manifest.identityPrincipalId -and $role.scope -ieq $manifest.registryResourceId -and
                $role.roleDefinitionId -imatch '^/subscriptions/[a-f0-9-]{36}/providers/Microsoft.Authorization/roleDefinitions/7f951dda-4ed3-4680-a7ca-43fe172d538d\z') {
                $roleVisible = $true
            }
        }
        if ($roleVisible) { break }
        if ($attempt -lt 11) { Start-Sleep -Seconds 10 }
    }
    if (-not $roleVisible) { throw 'AcrPull is not visible at the exact registry scope. Check RBAC permissions and propagation; rerun explicitly.' }
    $runtimeArguments = @('--resource-group', $ResourceGroupName, '--name', "$NamePrefix-demo-runtime",
        '--template-file', (Join-Path $artifactDirectory 'runtime.json'), '--parameters', "location=$Location", "namePrefix=$NamePrefix",
        "appImage=$($manifest.appImage)", "intervalSeconds=$IntervalSeconds", "metricsIngestionEndpoint=$($manifest.metricsIngestionEndpoint)")
    $phase = 'runtime ownership preflight'
    $preview = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz (@('deployment', 'group', 'what-if') + $runtimeArguments +
        @('--result-format', 'FullResourcePayloads', '--no-pretty-print')) $SubscriptionId $phase -TimeoutSeconds 600)
    Write-DemoJson (Join-Path $artifactDirectory 'runtime.what-if.json') $preview
    Assert-DemoPreview $preview $metadata -Runtime
    Assert-DemoOwnership @(Get-DemoTarget $manifest) @(Get-DemoLiveResource $SubscriptionId $ResourceGroupName) $manifest.owner
    $phase = 'runtime deployment'
    for ($attempt = 0; $attempt -lt 4; $attempt++) {
        try {
            $runtime = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz (@('deployment', 'group', 'create') + $runtimeArguments) $SubscriptionId $phase -TimeoutSeconds 900)
            break
        }
        catch {
            if (-not $_.Exception.Data['ImagePullFailure'] -or $attempt -eq 3) { throw }
            # The generic native helper flags image errors, including permanent auth failures.
            # Inspect structured ARM failure codes before treating one as propagation/transient.
            $operationJson = Invoke-DemoAz @('deployment', 'operation', 'group', 'list', '--resource-group', $ResourceGroupName,
                '--name', "$NamePrefix-demo-runtime") $SubscriptionId 'runtime failure classification' -TimeoutSeconds 60
            $operations = (ConvertFrom-EvaluatorConfigurationJson ('{"items":' + $operationJson + '}')).items
            if ($operations -isnot [array]) { throw 'Invalid deployment operation response; no retry is safe.' }
            Write-DemoJson (Join-Path $artifactDirectory 'runtime.failure-operations.json') $operations
            $errors = @($operations | Where-Object { $_.properties.provisioningState -ceq 'Failed' } |
                ForEach-Object { $_.properties.statusMessage })
            $errorJson = ConvertTo-Json -InputObject $errors -Depth 64 -Compress
            if ($errors.Count -eq 0 -or $errorJson -notmatch '"code"\s*:\s*"(InaccessibleImage|RegistryErrorResponse|FailedToPullImage)"' -or
                $errorJson -match 'Unauthorized|Forbidden|AuthorizationFailed|Authentication|InvalidCredentials|InvalidImageRegistryCredential|\b(401|403)\b|denied|manifest unknown|not found|invalid') {
                throw 'Image pull failed with authentication, permission, invalid-image or unrecognized diagnostics; no retry is safe.'
            }
            Write-Warning 'Recognized transient ACI image-pull failure. Waiting for AcrPull propagation; no compute deletion/recreation.'
            Start-Sleep -Seconds 20
        }
    }
    if ($runtime.properties.provisioningState -cne 'Succeeded' -or $runtime.properties.outputs.Count -ne 2 -or
        $runtime.properties.outputs.containerGroupResourceId.type -ine 'String' -or $runtime.properties.outputs.containerGroupName.type -ine 'String' -or
        $runtime.properties.outputs.containerGroupResourceId.value -ine $manifest.containerGroupResourceId -or
        $runtime.properties.outputs.containerGroupName.value -cne $manifest.containerGroupName) { throw 'Runtime deployment did not return the expected successful group.' }
    $manifest.status = 'RuntimeSucceeded'
    Write-DemoJson $ManifestPath $manifest
    $phase = 'three-container running state'
    $running = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $group = ConvertFrom-EvaluatorConfigurationJson (Invoke-DemoAz @('container', 'show', '--resource-group', $ResourceGroupName,
            '--name', $manifest.containerGroupName) $SubscriptionId $phase -TimeoutSeconds 60)
        if ($group.id -ine $manifest.containerGroupResourceId -or $group.containers -isnot [array] -or $group.containers.Count -ne 3 -or
            @($group.containers.name | Sort-Object) -join ',' -cne 'checkout-api,prometheus,traffic-generator' -or
            $group.provisioningState -cnotin @('Succeeded', 'Creating', 'Updating') -or
            $group.instanceView.state -cnotin @('Running', 'Pending')) { throw 'Invalid or terminal ACI group state. Inspect container state/events.' }
        foreach ($container in $group.containers) {
            if ($container.instanceView.restartCount -gt 0) {
                throw "Container '$($container.name)' has restarted ($($container.instanceView.restartCount) restarts). Inspect ACI state/events."
            }
            $currentEvents = @($container.instanceView.events | Where-Object {
                -not $_.lastTimestamp -or -not $container.instanceView.currentState.startTime -or
                ([DateTimeOffset]$_.lastTimestamp).ToUnixTimeSeconds() -ge
                    ([DateTimeOffset]$container.instanceView.currentState.startTime).ToUnixTimeSeconds()
            })
            if ($container.instanceView.currentState.state -cnotin @('Running', 'Waiting', 'Pending') -or
                (ConvertTo-Json -InputObject $currentEvents -Depth 32 -Compress) -match 'Unauthorized|Forbidden|AuthorizationFailed|Authentication|InvalidCredentials|\b(401|403)\b|denied' -or
                @($currentEvents | Where-Object {
                    $_.type -eq 'Warning' -and $_.name -notin @('Pulling', 'BackOff', 'FailedToPullImage') -and
                    -not ($container.instanceView.currentState.state -ceq 'Running' -and
                        $container.instanceView.restartCount -eq 0 -and
                        $_.name -eq 'Failed' -and $_.message -cmatch '^(Readiness|Liveness) probe failed:')
                }).Count) {
                throw 'Container failed with an unrecognized/terminal state. Inspect ACI events and permissions.'
            }
        }
        $running = $group.provisioningState -ceq 'Succeeded' -and $group.instanceView.state -ceq 'Running' -and
            @($group.containers | Where-Object { $_.instanceView.currentState.state -cne 'Running' }).Count -eq 0
        if ($running) { break }
        if ($attempt -lt 29) { Start-Sleep -Seconds 10 }
    }
    if (-not $running) { throw 'Timed out waiting for ARM Succeeded and all three containers Running.' }
    $manifest.status = 'Ready'
    Write-DemoJson $ManifestPath $manifest
    Write-Output "DEMO_READY. Manifest: $ManifestPath. Configure/deploy the evaluator independently; no evaluator deployment was invoked."
}
catch {
    if ($manifest) {
        $manifest.status = 'Failed'
        $manifest.failedPhase = $phase
        $manifest.failure = $_.Exception.Message
        Write-DemoJson $ManifestPath $manifest
    }
    [Console]::Error.WriteLine("Demo deployment failed ($phase): $($_.Exception.Message) Preserve the manifest; check Azure deployment operations, permissions, ACI events and Docker/ACR access. Incompatible ACI updates require separately authorized teardown/redeploy; compute is never recreated automatically.")
    exit 1
}
