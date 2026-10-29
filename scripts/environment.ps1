#Requires -Version 7.4
function ConvertFrom-EvaluatorConfigurationJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $Json)
    $document = $null
    try {
        $document = [Text.Json.JsonDocument]::Parse($Json)
        Assert-EvaluatorJsonProperty -Element $document.RootElement
        return ConvertFrom-Json -InputObject $Json -AsHashtable -Depth 64
    }
    catch { throw 'Invalid configuration JSON.' }
    finally { if ($document) { $document.Dispose() } }
}

function Assert-EvaluatorJsonProperty {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Text.Json.JsonElement] $Element)
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw 'Duplicate JSON property.' }
            Assert-EvaluatorJsonProperty -Element $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-EvaluatorJsonProperty -Element $item }
    }
}

function ConvertTo-EvaluatorEnvironment {
    [CmdletBinding()]
    [OutputType([Collections.Generic.Dictionary[string, string]])]
    param(
        # Defer collection conversion until errors can be stripped of payloads.
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $Plugins,
        [Parameter(Mandatory)][AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $Models,
        [AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $HostSettings = @{},
        [AllowNull()][AllowEmptyString()][AllowEmptyCollection()][object] $GeneratedEnvironment = @{},
        [ValidateRange(1, 2147483647)][int] $MaxEnvironmentBytes = 32768
    )
    $ErrorActionPreference = 'Stop'
    try {
        if ($null -eq $Plugins -or $null -eq $Models -or $HostSettings -isnot [Collections.IDictionary]) {
            throw 'Plugin and model inputs and a host settings dictionary are required.'
        }
        [object[]] $pluginEntries = $Plugins
        [object[]] $modelEntries = $Models
        [Collections.IDictionary] $settings = $HostSettings
        $environment = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($group in @(@{ Prefix = 'SHE_PLUGIN_'; Entries = $pluginEntries }, @{ Prefix = 'SHE_MODEL_'; Entries = $modelEntries })) {
            if ($group.Entries.Count -eq 0) { throw 'At least one plugin and model are required.' }
            foreach ($entry in $group.Entries) {
                $fields = if ($entry -is [Collections.IDictionary]) { @($entry.Keys) } else { @($entry.PSObject.Properties.Name) }
                if ($fields.Count -ne 2 -or 'key' -cnotin $fields -or 'config' -cnotin $fields) { throw 'Entries require exactly key and config.' }
                $key = $entry.key
                if ($key -isnot [string] -or $key -cnotmatch '^[A-Z][A-Z0-9_]*\z') { throw 'Invalid configuration entry key.' }
                $name = $group.Prefix + $key
                if ($environment.ContainsKey($name)) { throw 'Duplicate configuration entry key.' }
                $config = $entry.config
                if ($null -eq $config -or ($config -isnot [Collections.IDictionary] -and $config -isnot [pscustomobject])) {
                    throw 'Each config must be a JSON object, not serialized text.'
                }
                $json = ConvertTo-Json -InputObject $config -Depth 64 -Compress -WarningAction Stop
                $document = [Text.Json.JsonDocument]::Parse($json)
                $document.Dispose()
                $environment.Add($name, $json)
            }
        }
        $allowed = @('SHE_DEFAULT_INTERVAL', 'SHE_DEFAULT_TIMEOUT', 'SHE_DEFAULT_TTL_MULTIPLIER',
            'SHE_REPORTS_PER_MINUTE', 'SHE_SECRET_CACHE_TTL', 'SHE_SHUTDOWN_TIMEOUT', 'SHE_LOG_LEVEL')
        foreach ($key in $settings.Keys) {
            if ($key -isnot [string] -or $key -cnotin $allowed) { throw 'Reserved or unknown host setting.' }
            $value = $settings[$key]
            if ($value -is [string]) { $text = $value }
            elseif ($value -is [ValueType] -and $value -is [IFormattable] -and $value -isnot [bool] -and $value -isnot [datetime]) {
                $text = $value.ToString($null, [Globalization.CultureInfo]::InvariantCulture)
            }
            else { throw 'Host settings must be invariant scalar strings or numbers.' }
            if ([string]::IsNullOrWhiteSpace($text) -or $text.IndexOfAny([char[]]"`r`n`0") -ge 0) { throw 'Invalid host setting value.' }
            switch -Exact ($key) {
                'SHE_LOG_LEVEL' {
                    if ($text -cnotin @('Trace', 'Debug', 'Information', 'Warning', 'Error', 'Critical', 'None')) { throw 'Invalid log level.' }
                }
                'SHE_REPORTS_PER_MINUTE' {
                    $number = 0
                    if ($text -cnotmatch '^[0-9]+\z' -or -not [int]::TryParse($text, [ref]$number) -or $number -lt 1 -or $number -gt 5999) {
                        throw 'Invalid report budget.'
                    }
                }
                'SHE_DEFAULT_TTL_MULTIPLIER' {
                    $number = 0.0
                    if (-not [double]::TryParse($text, [Globalization.NumberStyles]::AllowDecimalPoint,
                            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -or -not [double]::IsFinite($number) -or $number -le 0) {
                        throw 'Invalid TTL multiplier.'
                    }
                }
                default {
                    if ($text -cnotmatch '^([0-9]+)([smhd])\z') { throw 'Invalid host duration.' }
                    $count = 0L
                    if (-not [long]::TryParse($Matches[1], [ref]$count) -or $count -le 0) { throw 'Invalid host duration.' }
                    $ticks = @{ s = 10000000L; m = 600000000L; h = 36000000000L; d = 864000000000L }[$Matches[2]]
                    if ([decimal]$count * $ticks -gt [long]::MaxValue) { throw 'Host duration is out of range.' }
                    if ($key -ceq 'SHE_DEFAULT_INTERVAL' -and ($count * [decimal]$ticks -lt 100000000L -or $count * [decimal]$ticks -gt 864000000000L)) {
                        throw 'Default interval must be from 10s through 1d.'
                    }
                }
            }
            $environment.Add($key, $text)
        }
        if ($GeneratedEnvironment -isnot [Collections.IDictionary]) { throw 'Invalid generated environment.' }
        foreach ($key in $GeneratedEnvironment.Keys) {
            $value = $GeneratedEnvironment[$key]
            if ($key -cnotin @('AZURE_CLIENT_ID', 'APPLICATIONINSIGHTS_CONNECTION_STRING', 'SHE_PACKAGES_DIR') -or
                $value -isnot [string] -or [string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny([char[]]"`r`n`0") -ge 0) {
                throw 'Invalid generated environment.'
            }
            if ($key -ceq 'AZURE_CLIENT_ID' -and $value -cnotmatch '^[a-fA-F0-9]{8}-(?:[a-fA-F0-9]{4}-){3}[a-fA-F0-9]{12}\z') { throw 'Invalid identity.' }
            if ($key -ceq 'SHE_PACKAGES_DIR' -and $value -cne '/app/packages') { throw 'Invalid package directory.' }
            $environment.Add($key, $value)
        }
        $bytes = 1L
        foreach ($entry in $environment.GetEnumerator()) {
            $bytes += [Text.Encoding]::UTF8.GetByteCount($entry.Key + '=' + $entry.Value) + 1
        }
        if ($bytes -gt $MaxEnvironmentBytes) { throw 'Serialized environment exceeds the configured size budget.' }
        return $environment
    }
    catch { throw 'Invalid configuration input, entry keys, host settings or serialized environment size budget.' }
}
