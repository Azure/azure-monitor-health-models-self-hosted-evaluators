#Requires -Version 7.4
[CmdletBinding()]
param(
    [string] $OfflineNuGetDirectory,
    [string] $ResultsDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
. (Join-Path $PSScriptRoot 'environment.ps1')
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runId = 'she-test-' + [guid]::NewGuid().ToString('N')
$containers = [Collections.Generic.List[string]]::new()
$images = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$checks = [Collections.Generic.List[string]]::new()
$shutdowns = [Collections.Generic.List[object]]::new()
$network = $null
$previousImages = $null
$phase = 'prerequisites'
$restoreDirectory = $OfflineNuGetDirectory

function Assert-ContainerCheck([bool] $Condition, [string] $Name) {
    if (-not $Condition) { throw "Container acceptance failed ($Name)." }
    $checks.Add($Name)
    Write-Output "PASS: $Name"
}
function Invoke-ContainerDocker([string[]] $Arguments) {
    Invoke-EvaluatorCommand docker $Arguments 'local container acceptance' -TimeoutSeconds 900
}
function Get-ContainerSnapshot {
    $text = Invoke-ContainerDocker @('exec', $fixture, 'dotnet', '/fixtures/SelfHostedEvaluators.ContainerFixtures.dll', 'snapshot')
    return ($text | ConvertFrom-Json -AsHashtable)
}
function Wait-ContainerCondition([scriptblock] $Condition, [int] $Seconds = 30) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition)) {
        if ($watch.Elapsed.TotalSeconds -ge $Seconds) { throw 'Container acceptance condition timed out.' }
        Start-Sleep -Milliseconds 200
    }
}
function ConvertTo-ContainerModel([string] $Name, [string] $Origin, [object[]] $Signals) {
    return @{ schemaVersion = 1; name = $Name; healthModel = @{ dataPlaneUri = $Origin }; signals = $Signals }
}
function ConvertTo-ContainerSignal([string] $Name, [string] $Plugin, [hashtable] $Query, [string] $Interval = '1m') {
    $evaluation = if ($Query.ContainsKey('monitorId')) { @{ stateMap = @{ OK = 'Healthy'; Alert = 'Unhealthy' } } }
    else { @{ unhealthy = @{ operator = 'GreaterThan'; threshold = 10 } } }
    return @{ entity = 'fixture-entity'; name = $Name; plugin = $Plugin; query = $Query; evaluation = $evaluation; interval = $Interval }
}
function Start-ContainerWorker {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Starts only uniquely named test-owned containers and always removes them in finally.')]
    param([string] $Image, [object[]] $Plugins, [object[]] $Models, [string] $Suffix, [string] $ShutdownTimeout = '2s')
    $environment = ConvertTo-EvaluatorEnvironment -Plugins $Plugins -Models $Models -HostSettings @{ SHE_SHUTDOWN_TIMEOUT = $ShutdownTimeout }
    $file = Join-Path $ResultsDirectory ($Suffix + '.env')
    [IO.File]::WriteAllLines($file, @($environment.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }))
    $id = Invoke-ContainerDocker @('create', '--name', "$runId-$Suffix", '--label', "she.acceptance=$runId",
        '--network', $network, '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
        '--mount', "type=bind,source=$trust,target=/test-trust,readonly",
        '--env', 'SSL_CERT_FILE=/test-trust/ca.pem', '--env', 'IDENTITY_ENDPOINT=http://fixtures:8081/identity',
        '--env', 'IDENTITY_HEADER=container-fixture-only', '--env-file', $file, $Image)
    $containers.Add($id)
    $null = Invoke-ContainerDocker @('start', $id)
    Wait-ContainerCondition {
        try {
            $marker = Invoke-ContainerDocker @('exec', $id, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'ready')
            return $marker -ceq 'Evaluator probe ready succeeded.'
        }
        catch {
            $state = Invoke-ContainerDocker @('inspect', $id, '--format', '{{.State.Running}}')
            if ($state -ne 'true') { throw 'Worker exited before readiness.' }
            return $false
        }
    }
    return $id
}
function Stop-ContainerWorker {
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Signals only a container ID returned by this invocation, as the acceptance scenario requires.')]
    param([string] $Id)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $null = Invoke-ContainerDocker @('kill', '--signal', 'TERM', $Id)
    $notReady = $false
    try {
        $notReady = (Invoke-ContainerDocker @('exec', $Id, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'ready')) -cne 'Evaluator probe ready succeeded.'
    }
    catch { $notReady = $true }
    Assert-ContainerCheck $notReady 'readiness fails once SIGTERM starts'
    Wait-ContainerCondition { (Invoke-ContainerDocker @('inspect', $Id, '--format', '{{.State.Running}}')) -eq 'false' } 10
    $shutdowns.Add(@{ container = $Id; seconds = $watch.Elapsed.TotalSeconds })
    Assert-ContainerCheck ($watch.Elapsed.TotalSeconds -lt 8) 'SIGTERM returns within shared shutdown budget plus process overhead'
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $Id, '--format', '{{.State.ExitCode}}')) -eq '0') 'SIGTERM exits cleanly without SIGKILL'
}
function Build-ContainerImage([string[]] $Packages) {
    $options = @{ PackageDirectories = $Packages }
    if ($restoreDirectory) { $options.OfflineNuGetDirectory = $restoreDirectory }
    $result = & (Join-Path $PSScriptRoot 'build-image.ps1') @options
    if ($LASTEXITCODE -ne 0 -or $result.DigestKind -ne 'LocalImageId') { throw 'Actual Dockerfile build failed.' }
    $null = $images.Add($result.ImageReference)
    return $result.ImageReference
}

try {
    if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repo ("artifacts\container-tests\$runId") }
    $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Use a new results directory.' }
    $null = [IO.Directory]::CreateDirectory($ResultsDirectory)
    $server = Invoke-ContainerDocker @('info', '--format', '{{.OSType}}/{{.Architecture}}')
    Assert-ContainerCheck ($server -eq 'linux/x86_64') 'Linux amd64 Docker daemon'
    $previousImages = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    (Invoke-ContainerDocker @('image', 'ls', '--all', '--quiet', '--no-trunc')) -split '\r?\n' |
        ForEach-Object { $null = $previousImages.Add($_) }
    $sdk = 'mcr.microsoft.com/dotnet/sdk:10.0.403'
    $null = Invoke-ContainerDocker @('pull', $sdk)
    $phase = 'package publish and image build'
    $directories = foreach ($project in @('Plugins.Static', 'Plugins.Datadog', 'Plugins.SplunkObservability', 'Plugins.AzureMonitor')) {
        $directory = Join-Path $ResultsDirectory $project
        $null = Invoke-EvaluatorCommand dotnet @('publish', (Join-Path $repo "src\$project"), '--configuration', 'Release',
            '--output', $directory, '--no-self-contained', '-p:UseAppHost=false', '--verbosity', 'quiet') 'package publish'
        $directory
    }
    $image = Build-ContainerImage $directories
    $repeat = Build-ContainerImage $directories
    Assert-ContainerCheck ($image -ceq $repeat) 'identical selected-package inputs reuse immutable image ID'
    $staticOnly = Build-ContainerImage @($directories[0])
    Assert-ContainerCheck ($staticOnly -cne $image) 'package selection changes image ID'
    $changed = Join-Path $ResultsDirectory 'changed-static'
    $null = Invoke-EvaluatorCommand dotnet @('publish', (Join-Path $repo 'src\Plugins.Static'), '--configuration', 'Release',
        '--output', $changed, '--no-self-contained', '-p:UseAppHost=false', "-p:InformationalVersion=1.0.0+$runId",
        '--verbosity', 'quiet') 'changed package publish'
    $changedImage = Build-ContainerImage (@($changed) + @($directories | Select-Object -Skip 1))
    Assert-ContainerCheck ($changedImage -cne $image) 'compiled package byte change changes image ID'
    $metadata = Invoke-ContainerDocker @('image', 'inspect', $image)
    $metadata | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'image-inspect.json')
    $config = ($metadata | ConvertFrom-Json)[0].Config
    Assert-ContainerCheck ($config.User -ceq '1654' -and -not $config.ExposedPorts -and -not $config.Volumes) 'non-root image has no declared ports or volumes'
    Assert-ContainerCheck (($config.Entrypoint -join ' ') -ceq '/usr/share/dotnet/dotnet /app/host/SelfHostedEvaluators.Host.dll') 'published Host is the actual PID 1 entrypoint'
    $catalog = Invoke-ContainerDocker @('run', '--rm', '--network', 'none', '--read-only', $image, 'validate-packages', '/app/packages')
    Assert-ContainerCheck ($catalog -ceq 'Package catalog accepted.') 'all selected packages load in real Linux image'
    $shellAbsent = $false
    try { $null = Invoke-ContainerDocker @('run', '--rm', '--network', 'none', '--entrypoint', '/bin/sh', $image, '-c', 'exit 0') }
    catch { $shellAbsent = $true }
    Assert-ContainerCheck $shellAbsent 'chiseled runtime has no shell'

    $phase = 'isolated HTTPS and managed identity fixtures'
    $publish = Join-Path $ResultsDirectory 'fixtures'
    $null = Invoke-EvaluatorCommand dotnet @('publish', (Join-Path $repo 'tests\SelfHostedEvaluators.ContainerFixtures'),
        '--configuration', 'Release', '--output', $publish, '--verbosity', 'quiet') 'container fixture publish'
    $trust = Join-Path $ResultsDirectory 'trust'
    $null = [IO.Directory]::CreateDirectory($trust)
    $key = [Security.Cryptography.RSA]::Create(2048)
    try {
        $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=fixtures', $key,
            [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $names = [Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $names.AddDnsName('fixtures')
        $request.CertificateExtensions.Add($names.Build())
        $request.CertificateExtensions.Add([Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($true, $false, 0, $true))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddHours(2))
        try {
            [IO.File]::WriteAllBytes((Join-Path $trust 'fixture.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx))
            [IO.File]::WriteAllText((Join-Path $trust 'ca.pem'), $certificate.ExportCertificatePem())
        }
        finally { $certificate.Dispose() }
    }
    finally { $key.Dispose() }
    $network = Invoke-ContainerDocker @('network', 'create', '--internal', '--label', "she.acceptance=$runId", $runId)
    $fixture = Invoke-ContainerDocker @('create', '--name', "$runId-fixtures", '--label', "she.acceptance=$runId",
        '--network', $network, '--network-alias', 'fixtures', '--read-only',
        '--mount', "type=bind,source=$publish,target=/fixtures,readonly", '--mount', "type=bind,source=$trust,target=/test-trust,readonly",
        $sdk, 'dotnet', '/fixtures/SelfHostedEvaluators.ContainerFixtures.dll', 'serve', '/test-trust/fixture.pfx')
    $containers.Add($fixture)
    $null = Invoke-ContainerDocker @('start', $fixture)
    Wait-ContainerCondition {
        try { return (Invoke-ContainerDocker @('exec', $fixture, 'dotnet', '/fixtures/SelfHostedEvaluators.ContainerFixtures.dll', 'ready')) -eq 'ready' }
        catch { return $false }
    }
    $plugins = @(
        @{ key = 'STATIC'; config = @{ schemaVersion = 1; name = 'static-one'; package = 'static'; endpoints = @{ default = 'https://fixtures:8443' }; auth = @{ type = 'none' }; rateLimit = @{ requests = 100; per = '1s' }; batch = @{ maxSize = 3 } } },
        @{ key = 'EU'; config = @{ schemaVersion = 1; name = 'datadog-eu'; package = 'datadog'; endpoints = @{ default = 'https://fixtures:8443' }; auth = @{ type = 'none' }; rateLimit = @{ requests = 100; per = '1s' } } },
        @{ key = 'US'; config = @{ schemaVersion = 1; name = 'datadog-us'; package = 'datadog'; endpoints = @{ default = 'https://fixtures:8443' }; auth = @{ type = 'none' }; rateLimit = @{ requests = 100; per = '1s' } } },
        @{ key = 'SPLUNK'; config = @{ schemaVersion = 1; name = 'splunk-one'; package = 'splunk-observability'; endpoints = @{ api = 'https://fixtures:8443'; stream = 'https://fixtures:8443' }; auth = @{ type = 'none' }; rateLimit = @{ requests = 100; per = '1s' }; timeout = '30s' } }
    )
    $signals = @(
        (ConvertTo-ContainerSignal 'static-old' 'static-one' @{ kind = 'observation'; value = 5 } '10s'),
        (ConvertTo-ContainerSignal 'no-data' 'static-one' @{ kind = 'noData'; reason = 'fixture' }),
        (ConvertTo-ContainerSignal 'failure' 'static-one' @{ kind = 'failure'; code = 'fixture'; message = 'fixture failure' }),
        (ConvertTo-ContainerSignal 'dd-eu' 'datadog-eu' @{ monitorId = 1 }),
        (ConvertTo-ContainerSignal 'dd-us' 'datadog-us' @{ monitorId = 2 }),
        (ConvertTo-ContainerSignal 'splunk' 'splunk-one' @{ metric = 'cpu' })
    )
    $models = @(
        @{ key = 'ONE'; config = (ConvertTo-ContainerModel 'model-one' 'https://fixtures:8443' $signals) },
        @{ key = 'TWO'; config = (ConvertTo-ContainerModel 'model-two' 'https://fixtures:8444' @((ConvertTo-ContainerSignal 'second-model' 'static-one' @{ kind = 'observation'; value = 5 }))) }
    )
    $phase = 'exact-image offline validation'
    $accepted = & (Join-Path $PSScriptRoot 'validate-config.ps1') -ImageReference $image -Plugins $plugins -Models $models
    Assert-ContainerCheck ($LASTEXITCODE -eq 0 -and $accepted -ceq 'Configuration accepted by the selected image.') 'exact image validates complete configuration with network disabled'
    $rejected = & (Join-Path $PSScriptRoot 'validate-config.ps1') -ImageReference $staticOnly -Plugins $plugins -Models $models 2>&1
    Assert-ContainerCheck ($LASTEXITCODE -ne 0 -and ($rejected -join '') -notmatch 'Configuration accepted') 'missing selected package rejects configuration in actual container'
    $largeSignals = @(1..40 | ForEach-Object { ConvertTo-ContainerSignal "large-signal-$_" 'static-one' @{ kind = 'observation'; value = 5; detail = ('x' * 150) } })
    $largeModels = @(@{ key = 'LARGE'; config = (ConvertTo-ContainerModel 'large-model' 'https://fixtures:8443' $largeSignals) })
    $largeAccepted = & (Join-Path $PSScriptRoot 'validate-config.ps1') -ImageReference $image -Plugins $plugins -Models $largeModels
    Assert-ContainerCheck ($LASTEXITCODE -eq 0 -and $largeAccepted -ceq 'Configuration accepted by the selected image.') 'real Linux environment accepts a realistic forty-signal model'

    $phase = 'real production image pipeline'
    $worker = Start-ContainerWorker $image $plugins $models 'baseline'
    $live = Invoke-ContainerDocker @('exec', $worker, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'live')
    Assert-ContainerCheck ($live -ceq 'Evaluator probe live succeeded.') 'real liveness exec probe'
    Wait-ContainerCondition {
        @((Get-ContainerSnapshot).reports | Where-Object status -EQ 202 |
            ForEach-Object { ($_.body | ConvertFrom-Json).signalName } | Select-Object -Unique).Count -eq 7
    } 45
    $snapshot = Get-ContainerSnapshot
    $snapshot | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'pipeline.json')
    Assert-ContainerCheck ($snapshot.identities -gt 0 -and $snapshot.unexpected -eq 0) 'production managed-identity SDK and HTTPS routes reach only isolated fixtures'
    Assert-ContainerCheck ($snapshot.remoteChecks -gt 0) 'remote model-check HTTP 404 warnings do not gate readiness or accepted reports'
    foreach ($group in @($snapshot.reports | Group-Object id)) {
        Assert-ContainerCheck (@($group.Group | Select-Object -ExpandProperty body -Unique).Count -eq 1) 'HTTP 429 and 503 retries preserve report ID and body'
        if (@($group.Group | Where-Object status -EQ 202).Count) {
            Assert-ContainerCheck (($group.Group.status -join ',') -ceq '429,503,202') 'transient model failures eventually receive actual HTTP 202'
        }
    }
    foreach ($report in $snapshot.reports) {
        Assert-ContainerCheck ($report.length -gt 0 -and -not $report.transferEncoding -and $report.query -ceq '?api-version=2026-06-01-preview') 'buffered data-plane request uses exact API version'
    }
    $actual = @($snapshot.reports | Where-Object status -EQ 202 | ForEach-Object { $_.body | ConvertFrom-Json -AsHashtable })
    $expected = @{ 'static-old' = 'Healthy'; 'no-data' = 'Unknown'; failure = 'Unknown'; 'dd-eu' = 'Healthy'; 'dd-us' = 'Unhealthy'; splunk = 'Unhealthy'; 'second-model' = 'Healthy' }
    foreach ($name in $expected.Keys) {
        Assert-ContainerCheck (@($actual | Where-Object { $_.signalName -ceq $name -and $_.healthState -ceq $expected[$name] }).Count -ge 1) "actual package result $name"
    }
    $inspect = (Invoke-ContainerDocker @('inspect', $worker) | ConvertFrom-Json -AsHashtable)[0]
    Assert-ContainerCheck ($inspect.HostConfig.PortBindings.Count -eq 0 -and @($inspect.Mounts | Where-Object Destination -EQ '/app/packages').Count -eq 0) 'worker has no published ports or package mounts'
    Stop-ContainerWorker $worker

    $phase = 'configuration-only replacement and rollback'
    $null = Invoke-ContainerDocker @('exec', $fixture, 'dotnet', '/fixtures/SelfHostedEvaluators.ContainerFixtures.dll', 'reset')
    $replacement = @(@{ key = 'ONE'; config = (ConvertTo-ContainerModel 'model-one' 'https://fixtures:8443' @((ConvertTo-ContainerSignal 'static-new' 'static-one' @{ kind = 'observation'; value = 42 }))) })
    $worker = Start-ContainerWorker $image $plugins $replacement 'replacement'
    Wait-ContainerCondition { @((Get-ContainerSnapshot).reports | Where-Object status -EQ 202).Count -ge 1 }
    Start-Sleep -Seconds 21
    $replacementSnapshot = Get-ContainerSnapshot
    $replacementSnapshot | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'replacement.json')
    $received = @($replacementSnapshot.reports | Where-Object status -EQ 202 | ForEach-Object { $_.body | ConvertFrom-Json -AsHashtable })
    # Startup polls immediately; the first aligned minute tick may follow within this window.
    Assert-ContainerCheck ($received.Count -in 1, 2 -and @($received | Where-Object {
            $_.signalName -cne 'static-new' -or $_.healthState -cne 'Unhealthy' -or $_.expiresInMinutes -ne 3
        }).Count -eq 0) 'new process uses replacement signals and one-minute cadence, not removed ten-second signal'
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.Image}}')) -ceq $image) 'configuration-only replacement reuses exact image ID'
    Stop-ContainerWorker $worker
    $worker = Start-ContainerWorker $changedImage $plugins $replacement 'package-update'
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.Image}}')) -ceq $changedImage) 'rebuilt selected-package image starts successfully'
    Stop-ContainerWorker $worker
    $worker = Start-ContainerWorker $image $plugins $models 'rollback'
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.Image}}')) -ceq $image) 'previous image and complete configuration can be restored locally'
    Stop-ContainerWorker $worker

    $phase = 'Linux SIGTERM and forced termination during active SSE'
    $partial = @(@{ key = 'ONE'; config = (ConvertTo-ContainerModel 'model-one' 'https://fixtures:8443' @((ConvertTo-ContainerSignal 'partial' 'splunk-one' @{ metric = 'partial' }))) })
    $before = (Get-ContainerSnapshot).streamsStarted
    $worker = Start-ContainerWorker $image $plugins $partial 'sigterm' '5s'
    Wait-ContainerCondition { (Get-ContainerSnapshot).streamsStarted -gt $before }
    Stop-ContainerWorker $worker
    Wait-ContainerCondition { $s = Get-ContainerSnapshot; $s.streamsAborted -eq $s.streamsStarted }
    Assert-ContainerCheck $true 'SIGTERM closes flushed partial SSE without waiting for normal poll timeout'
    $before = (Get-ContainerSnapshot).streamsStarted
    $worker = Start-ContainerWorker $image $plugins $partial 'sigkill'
    Wait-ContainerCondition { (Get-ContainerSnapshot).streamsStarted -gt $before }
    $null = Invoke-ContainerDocker @('kill', '--signal', 'KILL', $worker)
    Wait-ContainerCondition { (Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.State.Running}}')) -eq 'false' }
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.State.ExitCode}}')) -eq '137') 'forced SIGKILL is distinguishable from graceful shutdown'
    $worker = Start-ContainerWorker $image $plugins $replacement 'after-kill'
    Assert-ContainerCheck ((Invoke-ContainerDocker @('exec', $worker, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'ready')) -ceq 'Evaluator probe ready succeeded.') 'fresh worker starts after forced termination'
    $phase = 'local probe-failure and explicit restart'
    $beforeStart = Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.State.StartedAt}}')
    $null = Invoke-ContainerDocker @('kill', '--signal', 'STOP', $worker)
    try {
        $liveFailed = $false
        try { $null = Invoke-ContainerDocker @('exec', $worker, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'live') }
        catch { $liveFailed = $true }
        Assert-ContainerCheck $liveFailed 'actual loopback liveness probe fails when PID 1 is stalled'
    }
    finally { $null = Invoke-ContainerDocker @('kill', '--signal', 'CONT', $worker) }
    $null = Invoke-ContainerDocker @('restart', '--timeout', '8', $worker)
    Wait-ContainerCondition {
        try { return (Invoke-ContainerDocker @('exec', $worker, '/usr/share/dotnet/dotnet', '/app/host/SelfHostedEvaluators.Host.dll', 'probe', 'ready')) -ceq 'Evaluator probe ready succeeded.' }
        catch { return $false }
    }
    Assert-ContainerCheck ((Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.State.StartedAt}}')) -cne $beforeStart -and
        (Invoke-ContainerDocker @('inspect', $worker, '--format', '{{.Image}}')) -ceq $image) 'explicit local restart restores probes with unchanged image and configuration'
    Stop-ContainerWorker $worker
    @{ runId = $runId; image = $image; changedImage = $changedImage; staticOnly = $staticOnly; checks = $checks.ToArray(); shutdowns = $shutdowns.ToArray() } |
        ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $ResultsDirectory 'acceptance.json')
    Write-Output "Container acceptance passed: $($checks.Count) checks. Evidence: $ResultsDirectory"
    Write-Output 'No registry manifest push, Azure acceptance or authoritative model snapshot/TTL readback was performed.'
}
catch { Write-Error "Container acceptance failed ($phase). Inspect the task-owned evidence directory." -ErrorAction Continue; exit 1 }
finally {
    foreach ($id in $containers) {
        try {
            Invoke-ContainerDocker @('logs', $id) | Set-Content -LiteralPath (Join-Path $ResultsDirectory ($id + '.log'))
            Invoke-ContainerDocker @('inspect', $id) | Set-Content -LiteralPath (Join-Path $ResultsDirectory ($id + '.json'))
        }
        catch { Write-Warning 'Could not capture an owned container log or state.' }
        try { $null = Invoke-ContainerDocker @('rm', '--force', $id) }
        catch { Write-Warning "Could not remove owned container $id." }
    }
    if ($network) {
        try { $null = Invoke-ContainerDocker @('network', 'rm', $network) }
        catch { Write-Warning "Could not remove owned network $network." }
    }
    foreach ($imageId in $images) {
        if ($null -ne $previousImages -and -not $previousImages.Contains($imageId)) {
            try { $null = Invoke-ContainerDocker @('image', 'rm', $imageId) }
            catch { Write-Warning "Could not remove owned image $imageId." }
        }
    }
}
