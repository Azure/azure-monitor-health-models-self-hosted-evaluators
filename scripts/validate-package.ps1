#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageDirectory,
    [Parameter(Mandatory)][string] $StagingDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
$work = $null
$stagingWork = $null
try {
    $work = New-EvaluatorWorkDirectory
    $packages = Join-Path $work 'packages'
    $package = Copy-EvaluatorPackage -PackageDirectory $PackageDirectory -PackagesRoot $packages
    $before = Get-EvaluatorPackageFingerprint $package.PackageDirectory
    Test-EvaluatorPackageCatalog $packages
    if ($before -cne (Get-EvaluatorPackageFingerprint $package.PackageDirectory)) { throw 'Validation modified the published package.' }
    $destinationRoot = [IO.Path]::GetFullPath($StagingDirectory)
    $destination = Join-Path (Join-Path $destinationRoot $package.Id) $package.Version
    if (Test-Path -LiteralPath $destination) { throw 'Staged version already exists.' }
    $stagingWork = Join-Path $destinationRoot ('.stage-' + [guid]::NewGuid().ToString('N'))
    $staged = Copy-EvaluatorPackage -PackageDirectory $package.PackageDirectory -PackagesRoot $stagingWork
    if ($before -cne (Get-EvaluatorPackageFingerprint $staged.PackageDirectory)) { throw 'Staging verification failed.' }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))
    [IO.Directory]::Move($staged.PackageDirectory, $destination)
    [pscustomobject]@{
        Id = $staged.Id; Version = $staged.Version; PackageDirectory = $destination
    }
}
catch {
    [Console]::Error.WriteLine('Package validation failed. Supply a compatible published package; no package was staged.')
    exit 1
}
finally {
    if ($stagingWork -and (Test-Path -LiteralPath $stagingWork)) { Remove-Item -LiteralPath $stagingWork -Recurse -Force }
    if ($work -and [IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
