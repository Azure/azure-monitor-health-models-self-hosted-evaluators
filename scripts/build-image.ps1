#Requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateCount(1, 1000)][string[]] $PackageDirectories,
    [switch] $Push,
    [string] $Registry,
    [string] $Repository,
    [string] $OfflineNuGetDirectory,
    [ValidateSet('linux/amd64', 'linux/arm64')][string] $Platform = 'linux/amd64'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'delivery-common.ps1')
$work = $null
try {
    if ($Push) {
        if ($Registry -cnotmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?\z' -or
            $Repository -cnotmatch '^[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*\z') {
            throw 'Explicit valid registry and repository are required to push.'
        }
    }
    elseif ($Registry -or $Repository) { throw 'Registry inputs require the explicit Push switch.' }
    $null = Invoke-EvaluatorCommand -Command docker -Arguments @('version', '--format', '{{.Server.Version}}') -Operation 'Docker daemon availability'
    $work = New-EvaluatorWorkDirectory
    $context = Join-Path $work 'context'
    $packages = Join-Path $context '_image\packages'
    $null = [IO.Directory]::CreateDirectory($packages)
    $restore = Join-Path $context '_build\restore'
    $null = [IO.Directory]::CreateDirectory($restore)
    [IO.File]::WriteAllText((Join-Path $restore '.keep'), '')
    if ($OfflineNuGetDirectory) {
        $feed = Get-Item -LiteralPath $OfflineNuGetDirectory -Force
        if (-not $feed.PSIsContainer) { throw 'Offline restore requires a trusted package directory.' }
        for ($ancestor = $feed; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked restore source.' }
        }
        $children = @(Get-ChildItem -LiteralPath $feed.FullName -Recurse -Force)
        if (@($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Linked restore source.' }
        $archives = @($children | Where-Object { -not $_.PSIsContainer -and $_.Extension -ceq '.nupkg' })
        if ($archives.Count -eq 0) { throw 'Offline restore requires package archives.' }
        foreach ($archive in $archives) {
            $target = Join-Path $restore $archive.Name
            if (Test-Path -LiteralPath $target) { throw 'Duplicate offline package filename.' }
            Copy-Item -LiteralPath $archive.FullName -Destination $target
        }
    }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($directory in $PackageDirectories) {
        $package = Copy-EvaluatorPackage -PackageDirectory $directory -PackagesRoot $packages
        if (-not $ids.Add($package.Id)) { throw 'Select exactly one version per package ID.' }
    }
    Test-EvaluatorPackageCatalog $packages
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    foreach ($name in @('Dockerfile', '.dockerignore', 'global.json', 'Directory.Build.props', 'nuget.config')) {
        Copy-Item -LiteralPath (Join-Path $root $name) -Destination $context
    }
    $source = Join-Path $context 'src'
    $null = [IO.Directory]::CreateDirectory($source)
    foreach ($project in @('Host', 'Core', 'Plugins.Abstractions')) {
        $sourceProject = Join-Path $root "src\$project"
        $targetProject = Join-Path $source $project
        $null = [IO.Directory]::CreateDirectory($targetProject)
        Get-ChildItem -LiteralPath $sourceProject -Recurse -File -Force | Where-Object {
            [IO.Path]::GetRelativePath($sourceProject, $_.FullName) -notmatch '(^|[\\/])(bin|obj)([\\/]|$)'
        } | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source file.' }
            $target = Join-Path $targetProject ([IO.Path]::GetRelativePath($sourceProject, $_.FullName))
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target))
            Copy-Item -LiteralPath $_.FullName -Destination $target
        }
    }
    $metadata = Join-Path $work 'metadata.json'
    $iid = Join-Path $work 'image.id'
    $arguments = @('buildx', 'build', '--platform', $Platform, '--metadata-file', $metadata, '--provenance=false')
    if ($OfflineNuGetDirectory) { $arguments += @('--network', 'none') }
    if ($Push) {
        $tag = "$Registry/${Repository}:build-$([guid]::NewGuid().ToString('N'))"
        $arguments += @('--push', '--tag', $tag)
    }
    else { $arguments += @('--load', '--iidfile', $iid) }
    $arguments += $context
    $null = Invoke-EvaluatorCommand -Command docker -Arguments $arguments -Operation 'image build'
    if ($Push) {
        $digest = (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).'containerimage.digest'
        Assert-EvaluatorImageReference $digest
        $reference = "$Registry/$Repository@$digest"
        $manifest = Invoke-EvaluatorCommand -Command docker -Arguments @('buildx', 'imagetools', 'inspect', $reference,
            '--format', '{{json .Manifest}}') -Operation 'registry digest verification'
        if (($manifest | ConvertFrom-Json).digest -cne $digest) { throw 'Registry digest does not match.' }
        $kind = 'RegistryManifest'
    }
    else {
        $digest = ([IO.File]::ReadAllText($iid)).Trim()
        Assert-EvaluatorImageReference $digest
        $verified = Invoke-EvaluatorCommand -Command docker -Arguments @('image', 'inspect', '--format', '{{.Id}}', $digest) -Operation 'local digest verification'
        if ($verified.Trim() -cne $digest) { throw 'Local image digest does not match.' }
        $reference = $digest
        $kind = 'LocalImageId'
    }
    [pscustomobject]@{ ImageReference = $reference; Digest = $digest; DigestKind = $kind; Platform = $Platform }
}
catch {
    [Console]::Error.WriteLine('Image build failed. Check selected published packages, the trusted offline feed if supplied, Docker/buildx availability and explicit registry inputs. No verified image reference was returned.')
    exit 1
}
finally {
    if ($work -and [IO.Directory]::Exists($work)) { Remove-Item -LiteralPath $work -Recurse -Force }
}
