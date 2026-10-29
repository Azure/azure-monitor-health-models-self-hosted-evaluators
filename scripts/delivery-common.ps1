#Requires -Version 7.4
function New-EvaluatorWorkDirectory {
    [CmdletBinding()]
    [OutputType([string])]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Creates only an invocation-owned private scratch directory, always cleaned by the caller.')]
    param()
    $parent = Join-Path $PSScriptRoot '..\artifacts\delivery'
    $null = [IO.Directory]::CreateDirectory($parent)
    $path = Join-Path $parent ([guid]::NewGuid().ToString('N'))
    if ($IsWindows) {
        $directory = [IO.Directory]::CreateDirectory($path)
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true, $false)
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
        $rule = [Security.AccessControl.FileSystemAccessRule]::new($user, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
        Set-Acl -LiteralPath $directory.FullName -AclObject $acl
    }
    else {
        $null = [IO.Directory]::CreateDirectory($path,
            [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor [IO.UnixFileMode]::UserExecute)
    }
    return [IO.Path]::GetFullPath($path)
}

function Invoke-EvaluatorCommand {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string] $Command,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $Operation,
        [ValidateRange(1, 3600)][int] $TimeoutSeconds = 900,
        [switch] $FailOnStderr
    )
    $resolved = Get-Command $Command -ErrorAction SilentlyContinue
    if (-not $resolved) { throw "Required command is unavailable ($Operation)." }
    if ($resolved.CommandType -in @('Application', 'ExternalScript')) {
        # Launch through PowerShell to support Windows .cmd tools without cmd.exe string parsing.
        $quoted = @($resolved.Source) + $Arguments | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }
        $script = '& ' + ($quoted -join ' ') + '; exit $LASTEXITCODE'
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = (Get-Process -Id $PID).Path
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-EncodedCommand',
                [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script)))) { $start.ArgumentList.Add($argument) }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        try {
            $null = $process.Start()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
                $process.Kill($true)
                $process.WaitForExit()
                throw "Command timed out ($Operation)."
            }
            $output = $stdout.GetAwaiter().GetResult()
            $errorOutput = $stderr.GetAwaiter().GetResult()
            $global:LASTEXITCODE = $process.ExitCode
            if ($process.ExitCode -ne 0 -or ($FailOnStderr -and $errorOutput.Length -gt 0)) {
                $failure = [InvalidOperationException]::new("Command failed ($Operation).")
                if (($output + $errorOutput) -match '\b(InaccessibleImage|RegistryErrorResponse|FailedToPullImage)\b') {
                    $failure.Data['ImagePullFailure'] = $true
                }
                throw $failure
            }
            return $output.TrimEnd("`r", "`n")
        }
        finally { $process.Dispose() }
    }
    $output = & $Command @Arguments 2>&1
    if ($LASTEXITCODE -ne 0 -or ($FailOnStderr -and
            @($output | Where-Object { $_ -is [Management.Automation.ErrorRecord] }).Count -gt 0)) {
        $failure = [InvalidOperationException]::new("Command failed ($Operation).")
        if (($output -join "`n") -match '\b(InaccessibleImage|RegistryErrorResponse|FailedToPullImage)\b') {
            $failure.Data['ImagePullFailure'] = $true
        }
        throw $failure
    }
    return ($output -join "`n")
}

function Assert-EvaluatorImageReference {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ImageReference)
    if ($ImageReference -cnotmatch '^(?:[a-z0-9][a-z0-9.:/_-]*@)?sha256:[a-f0-9]{64}\z') {
        throw 'An immutable sha256 image reference is required.'
    }
}

function Copy-EvaluatorPackage {
    [CmdletBinding()]
    [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSUseShouldProcessForStateChangingFunctions', '', Justification = 'Internal snapshot operation; caller owns and cleans the destination.')]
    param(
        [Parameter(Mandatory)][string] $PackageDirectory,
        [Parameter(Mandatory)][string] $PackagesRoot
    )
    try {
        $source = Get-Item -LiteralPath $PackageDirectory -Force
        if (-not $source.PSIsContainer) { throw 'directory' }
        for ($ancestor = $source; $null -ne $ancestor; $ancestor = $ancestor.Parent) {
            if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'link' }
        }
        $children = @(Get-ChildItem -LiteralPath $source.FullName -Recurse -Force)
        if (@($children | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'link' }
        $manifest = Get-Content -LiteralPath (Join-Path $source.FullName 'plugin.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 64
        $id = $manifest.id
        $version = $manifest.version
        if ($id -isnot [string] -or $id -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9-]*\z' -or
            $version -isnot [string] -or $version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9a-zA-Z.-]+)?(?:\+[0-9a-zA-Z.-]+)?\z') { throw 'identity' }
        $target = Join-Path (Join-Path $PackagesRoot $id) $version
        if (Test-Path -LiteralPath $target) { throw 'duplicate' }
        $null = [IO.Directory]::CreateDirectory($target)
        Get-ChildItem -LiteralPath $source.FullName -Force | Copy-Item -Destination $target -Recurse -Force
        return [pscustomobject]@{ Id = $id; Version = $version; PackageDirectory = $target }
    }
    catch { throw 'Invalid, linked, duplicate or unreadable published package.' }
}

function Test-EvaluatorPackageCatalog {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $PackagesRoot)
    $project = Join-Path $PSScriptRoot '..\src\Host\Host.csproj'
    $null = Invoke-EvaluatorCommand -Command dotnet -Arguments @('run', '--disable-build-servers', '--project', $project, '--configuration', 'Release',
        '--no-launch-profile', '--', 'validate-packages', $PackagesRoot) -Operation 'published package catalog validation'
}

function Get-EvaluatorPackageFingerprint {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $PackageDirectory)
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    Get-ChildItem -LiteralPath $PackageDirectory -File -Recurse -Force | ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($PackageDirectory, $_.FullName).Replace('\', '/')
        $files[$relative] = $_.FullName
    }
    [string[]] $names = @($files.Keys)
    [Array]::Sort($names, [StringComparer]::Ordinal)
    $entries = @($names | ForEach-Object { $_ + ':' + (Get-FileHash -LiteralPath $files[$_] -Algorithm SHA256).Hash })
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($entries -join "`n")))
}

function Write-EvaluatorCanonicalJson {
    [CmdletBinding()]
    param([Text.Json.JsonElement] $Element, [Text.Json.Utf8JsonWriter] $Writer)
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $Writer.WriteStartObject()
        [string[]] $keys = @($Element.EnumerateObject() | ForEach-Object { $_.Name })
        [Array]::Sort($keys, [StringComparer]::Ordinal)
        foreach ($key in $keys) {
            $Writer.WritePropertyName($key)
            Write-EvaluatorCanonicalJson $Element.GetProperty($key) $Writer
        }
        $Writer.WriteEndObject()
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        $Writer.WriteStartArray()
        foreach ($item in $Element.EnumerateArray()) { Write-EvaluatorCanonicalJson $item $Writer }
        $Writer.WriteEndArray()
    }
    else { $Element.WriteTo($Writer) }
}

function Get-EvaluatorParameterRevision {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Json, [switch] $ArmParameterFile)
    $document = [Text.Json.JsonDocument]::Parse($Json)
    $stream = [IO.MemoryStream]::new()
    $writer = [Text.Json.Utf8JsonWriter]::new($stream)
    try {
        if ($ArmParameterFile) {
            $parameters = $document.RootElement.GetProperty('parameters')
            [string[]] $keys = @($parameters.EnumerateObject() | ForEach-Object { $_.Name })
            [Array]::Sort($keys, [StringComparer]::Ordinal)
            $writer.WriteStartObject()
            foreach ($key in $keys) {
                $writer.WritePropertyName($key)
                Write-EvaluatorCanonicalJson $parameters.GetProperty($key).GetProperty('value') $writer
            }
            $writer.WriteEndObject()
        }
        else { Write-EvaluatorCanonicalJson $document.RootElement $writer }
        $writer.Flush()
        return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream.ToArray()))
    }
    finally { $writer.Dispose(); $stream.Dispose(); $document.Dispose() }
}
