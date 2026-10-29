#Requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Objects')]
param(
    [Parameter(Mandatory)][string] $ImageReference,
    # Collection binding can echo payloads before the redacted boundary runs.
    [Parameter(Mandatory, ParameterSetName = 'Objects')][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $Plugins,
    [Parameter(Mandatory, ParameterSetName = 'Objects')][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $Models,
    [Parameter(ParameterSetName = 'Objects')][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $HostSettings = @{},
    [Parameter(Mandatory, ParameterSetName = 'File')][string] $ConfigurationFile,
    [AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $GeneratedEnvironment = @{},
    [string] $RolloutOwner,
    [ValidateRange(1, 2147483647)][int] $MaxEnvironmentBytes = 32768
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
. (Join-Path $PSScriptRoot 'environment.ps1')
$work = $null
try {
    Assert-EvaluatorImageReference $ImageReference
    $containerOptions = @()
    if ($RolloutOwner) {
        $ownerGuid = [guid]::Empty
        if (-not [guid]::TryParseExact($RolloutOwner, 'D', [ref]$ownerGuid) -or $ownerGuid -eq [guid]::Empty) { throw 'Invalid rollout owner.' }
        $containerOptions = @('--label', "she.rollout=$RolloutOwner")
    }
    if ($PSCmdlet.ParameterSetName -eq 'File') {
        try {
            $inputObject = ConvertFrom-EvaluatorConfigurationJson -Json (Get-Content -LiteralPath $ConfigurationFile -Raw)
            if ($inputObject -isnot [Collections.IDictionary] -or
                @($inputObject.Keys | Where-Object { $_ -cnotin @('plugins', 'models', 'hostSettings') }).Count -ne 0) { throw 'shape' }
            $Plugins = $inputObject.plugins
            $Models = $inputObject.models
            if ($inputObject.Contains('hostSettings')) { $HostSettings = $inputObject.hostSettings }
        }
        catch { throw 'Cannot read configuration input.' }
    }
    $environment = ConvertTo-EvaluatorEnvironment -Plugins $Plugins -Models $Models -HostSettings $HostSettings `
        -GeneratedEnvironment $GeneratedEnvironment -MaxEnvironmentBytes $MaxEnvironmentBytes
    $work = New-EvaluatorWorkDirectory
    $file = Join-Path $work 'configuration.env'
    $lines = @($environment.GetEnumerator() | Sort-Object Key -CaseSensitive | ForEach-Object { $_.Key + '=' + $_.Value })
    [IO.File]::WriteAllLines($file, $lines, [Text.UTF8Encoding]::new($false))
    $arguments = @('run', '--rm', '--pull', 'never', '--network', 'none',
        '--read-only', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges', '--env-file', $file) +
        $containerOptions + @($ImageReference, 'validate')
    $null = Invoke-EvaluatorCommand -Command docker -Arguments $arguments -Operation 'image configuration validation'
    Write-Output 'Configuration accepted by the selected image.'
}
catch {
    [Console]::Error.WriteLine('Configuration validation failed. Check input, size budget, Docker availability and image compatibility.')
    exit 1
}
finally {
    if ($work -and [IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
