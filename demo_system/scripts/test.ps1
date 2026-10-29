#Requires -Version 7.4
[CmdletBinding()]
param(
    [string] $BicepPath = $env:BICEP_CLI_PATH
)
$ErrorActionPreference = 'Stop'
$demoDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$originalBicep = $env:BICEP_CLI_PATH

function Get-DemoValidationTool {
    param([string] $Name, [string] $Hint)
    $tool = Get-Command -Name $Name -CommandType Application, ExternalScript -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $tool) { throw "$Hint is required but unavailable: $Name. Install/configure it explicitly; no checks are skipped." }
    return $tool.Source
}

function Invoke-DemoLocalCheck {
    param([string] $Command, [string[]] $Arguments, [string] $Operation, [int] $TimeoutSeconds = 120)
    $quoted = @($Command) + $Arguments | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }
    $script = '& ' + ($quoted -join ' ') + '; exit $LASTEXITCODE'
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.WorkingDirectory = $demoDirectory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand',
            [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script)))) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        $null = $process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.Kill($true)
            $process.WaitForExit()
            [Console]::Out.Write($stdout.GetAwaiter().GetResult())
            [Console]::Error.Write($stderr.GetAwaiter().GetResult())
            throw "$Operation timed out after $TimeoutSeconds seconds."
        }
        $output = $stdout.GetAwaiter().GetResult()
        [Console]::Error.Write($stderr.GetAwaiter().GetResult())
        if ($process.ExitCode -ne 0) {
            [Console]::Out.Write($output)
            throw "$Operation failed with exit code $($process.ExitCode). See tool diagnostics above."
        }
        return $output.TrimEnd("`r", "`n")
    }
    finally { $process.Dispose() }
}

try {
    if (-not $BicepPath) { $BicepPath = 'bicep' }
    $env:BICEP_CLI_PATH = Get-DemoValidationTool $BicepPath 'BicepPath (or BICEP_CLI_PATH/PATH)'
    $dotnet = Get-DemoValidationTool 'dotnet' '.NET SDK (dotnet on PATH)'
    try { Import-Module PSScriptAnalyzer -ErrorAction Stop }
    catch { throw 'PSScriptAnalyzer is required and must be importable. Install it explicitly or prepend its module parent to PSModulePath, including for subprocesses.' }

    $scripts = @(Get-ChildItem -LiteralPath $demoDirectory -Filter '*.ps1' -File -Recurse |
        Where-Object { [IO.Path]::GetRelativePath($demoDirectory, $_.FullName) -notmatch '(^|[\\/])(artifacts|bin|obj)([\\/]|$)' })
    if ($scripts.Count -eq 0) { throw 'No demo PowerShell scripts found.' }
    foreach ($file in $scripts) {
        $tokens = $null
        $parseErrors = $null
        $null = [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
        if ($parseErrors.Count) { throw "Parse errors in $($file.FullName): $($parseErrors -join '; ')" }
    }
    Write-Output "PowerShell syntax: $($scripts.Count) scripts passed."
    $issues = @($scripts | ForEach-Object { Invoke-ScriptAnalyzer -Path $_.FullName -Severity Warning, Error })
    if ($issues.Count) {
        $issues | Format-Table RuleName, Severity, ScriptName, Line, Message -Wrap | Out-String | Write-Output
        throw 'PSScriptAnalyzer Warning/Error checks failed.'
    }
    Write-Output "PSScriptAnalyzer: $($scripts.Count) scripts passed (Warning/Error)."
    $sdk = (Invoke-DemoLocalCheck $dotnet @('--version') '.NET SDK check').Trim()
    $pinnedSdk = (Get-Content -LiteralPath (Join-Path $demoDirectory '..\global.json') -Raw | ConvertFrom-Json).sdk.version
    if ($sdk -cne $pinnedSdk) { throw "The pinned .NET SDK $pinnedSdk is required; resolved $sdk." }
    Write-Output ".NET SDK: $sdk."

    $artifacts = Join-Path $demoDirectory "artifacts\validation\$([guid]::NewGuid().ToString('N'))"
    $null = [IO.Directory]::CreateDirectory($artifacts)
    Write-Output "Validation artifacts: $artifacts"
    foreach ($name in @('main', 'runtime')) {
        $output = Join-Path $artifacts "$name.json"
        Invoke-DemoLocalCheck $env:BICEP_CLI_PATH @('build', (Join-Path $demoDirectory "infra\$name.bicep"),
            '--no-restore', '--outfile', $output) "Bicep $name compilation"
        if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or (Get-Item -LiteralPath $output).Length -eq 0) {
            throw "Bicep $name compilation produced no template."
        }
        Write-Output "Bicep: $name.bicep compiled."
    }
    $project = Join-Path $demoDirectory 'tests\Checkout.Demo.Tests\Checkout.Demo.Tests.csproj'
    Invoke-DemoLocalCheck $dotnet @('test', $project, '--configuration', 'Release', '--no-restore', '--nologo',
        '--logger', 'console;verbosity=minimal', '--logger', 'trx;LogFileName=demo.trx',
        '--results-directory', $artifacts) 'Focused demo NUnit tests' -TimeoutSeconds 900
    $resultsPath = Join-Path $artifacts 'demo.trx'
    if (-not (Test-Path -LiteralPath $resultsPath -PathType Leaf)) { throw 'Missing test evidence: NUnit produced no demo.trx.' }
    [xml] $results = Get-Content -LiteralPath $resultsPath -Raw
    $counters = $results.TestRun.ResultSummary.Counters
    $testResults = @($results.TestRun.Results.UnitTestResult)
    if (-not $counters -or [int]$counters.total -le 0 -or [int]$counters.executed -ne [int]$counters.total -or
        [int]$counters.passed -ne [int]$counters.total -or [int]$counters.failed -ne 0 -or
        [int]$counters.notExecuted -ne 0 -or $testResults.Count -ne [int]$counters.total -or
        @($testResults | Where-Object { $_.outcome -cne 'Passed' }).Count) {
        throw 'Incomplete passing test evidence: require nonempty results, every discovered test executed/passed, and no failures or skips.'
    }
    Write-Output "Local validation passed: $($counters.passed) passed, 0 failed, 0 skipped. Evidence: $resultsPath"
}
catch {
    [Console]::Error.WriteLine("Demo local validation failed: $($_.Exception.Message)")
    exit 1
}
finally {
    $env:BICEP_CLI_PATH = $originalBicep
}
