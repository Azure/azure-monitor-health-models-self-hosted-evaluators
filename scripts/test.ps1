#Requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string] $Configuration = 'Release',
    [string] $ResultsDirectory,
    [string] $BicepPath = $env:BICEP_CLI_PATH,
    [ValidateRange(30, 3600)][int] $CommandTimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
$phase = 'prerequisites'
$locationChanged = $false
try {
    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if (-not $ResultsDirectory) { $ResultsDirectory = Join-Path $repo ('artifacts\test-runs\' + [guid]::NewGuid().ToString('N')) }
    $ResultsDirectory = [IO.Path]::GetFullPath($ResultsDirectory)
    if (Test-Path -LiteralPath $ResultsDirectory) { throw 'Use a fresh results directory; stale TRX files are not evidence.' }
    if (-not $BicepPath) { $BicepPath = 'bicep' }
    Push-Location -LiteralPath $repo
    $locationChanged = $true
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -or
        -not (Get-Command $BicepPath -ErrorAction SilentlyContinue)) { throw 'Install .NET 10 SDK and Bicep 0.48.1 or set BICEP_CLI_PATH.' }
    $sdk = Invoke-EvaluatorCommand dotnet @('--version') 'SDK detection' -TimeoutSeconds 30
    $pinnedSdk = (Get-Content -LiteralPath (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($sdk -cne $pinnedSdk) { throw 'The SDK pinned by global.json is required.' }
    Import-Module PSScriptAnalyzer -MinimumVersion 1.24.0 -ErrorAction Stop
    if (-not (Get-Module PSScriptAnalyzer)) { throw 'PSScriptAnalyzer 1.24.0 or later is required.' }
    $bicepVersion = Invoke-EvaluatorCommand $BicepPath @('--version') 'Bicep detection' -TimeoutSeconds 30
    if ($bicepVersion -notmatch '^Bicep CLI version 0\.48\.1\b') { throw 'The verified Bicep 0.48.1 snapshot tool is required.' }
    $null = [IO.Directory]::CreateDirectory($ResultsDirectory)
    $previousBicep = $env:BICEP_CLI_PATH
    $env:BICEP_CLI_PATH = $BicepPath
    try {
        $phase = 'solution build'
        $null = Invoke-EvaluatorCommand dotnet @('build', (Join-Path $repo 'SelfHostedEvaluators.slnx'),
            '--configuration', $Configuration, '--no-restore', '--verbosity', 'quiet') $phase -TimeoutSeconds $CommandTimeoutSeconds
        $phase = 'Bicep compile and lint'
        $null = Invoke-EvaluatorCommand $BicepPath @('build', (Join-Path $repo 'infra\main.bicep'),
            '--outfile', (Join-Path $ResultsDirectory 'main.json')) $phase -TimeoutSeconds 120 -FailOnStderr
        $null = Invoke-EvaluatorCommand $BicepPath @('build-params', (Join-Path $repo 'infra\main.bicepparam'),
            '--outfile', (Join-Path $ResultsDirectory 'main.parameters.json')) $phase -TimeoutSeconds 120 -FailOnStderr
        $phase = 'PowerShell analysis'
        $findings = @(Invoke-ScriptAnalyzer -Path $PSScriptRoot -Recurse -Severity Error, Warning)
        if ($findings.Count) {
            $findings | Select-Object RuleName, ScriptName, Line | Format-Table | Out-Host
            throw 'Script analysis reported errors or warnings.'
        }
        $total = 0L; $passed = 0L; $failed = 0L
        $projects = @(Get-ChildItem (Join-Path $repo 'tests') -Filter '*.Tests.csproj' -File -Recurse | Sort-Object FullName)
        if ($projects.Count -eq 0) { throw 'No test projects were found.' }
        foreach ($project in $projects) {
            $phase = 'tests: ' + $project.BaseName
            $name = $project.BaseName + '.trx'
            $null = Invoke-EvaluatorCommand dotnet @('test', $project.FullName, '--configuration', $Configuration,
                '--no-build', '--no-restore', '--logger', "trx;LogFileName=$name", '--results-directory', $ResultsDirectory,
                '--verbosity', 'quiet') $phase -TimeoutSeconds $CommandTimeoutSeconds
            [xml] $trx = Get-Content -LiteralPath (Join-Path $ResultsDirectory $name) -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            if (-not $counters -or [long]$counters.total -le 0 -or [long]$counters.failed -ne 0 -or
                [long]$counters.executed -ne [long]$counters.total -or [long]$counters.passed -ne [long]$counters.total) {
                throw 'Test evidence is missing, empty, skipped or failing.'
            }
            $total += [long]$counters.total; $passed += [long]$counters.passed; $failed += [long]$counters.failed
            Write-Output ($project.BaseName + ': ' + $counters.passed + '/' + $counters.total)
        }
        Write-Output "Total: $total; passed: $passed; failed: $failed. TRX: $ResultsDirectory"
        Write-Output 'Offline validation passed. Docker runtime, Linux SIGTERM and authorized Azure scenarios are separate gates.'
    }
    finally { $env:BICEP_CLI_PATH = $previousBicep }
}
catch { Write-Error "Validation failed ($phase). Check prerequisites and the per-project TRX evidence; no tools were installed." -ErrorAction Continue; exit 1 }
finally { if ($locationChanged) { Pop-Location } }
