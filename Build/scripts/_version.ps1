# Shared version source. Only formal package entry points reserve a daily version.
function ConvertTo-PureRdpVersion([string]$Value) {
    $value = $Value.Trim() -replace '^[vV]', ''
    if ($value -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
        throw 'Version must be numeric, e.g. 2.0.0 or v2.0.0.'
    }
    $parts = @($value.Split('.') | ForEach-Object {
        $number = 0
        if (-not [int]::TryParse($_, [ref]$number) -or $number -gt 65534) {
            throw 'Version components must be between 0 and 65534.'
        }
        $number
    })
    return $parts -join '.'
}

function Read-PureRdpVersionDocument([string]$VersionFile) {
    $document = [xml]::new()
    $document.PreserveWhitespace = $true
    $document.Load($VersionFile)
    foreach ($name in @('Version', 'FileVersion', 'LastPackageVersionDate')) {
        if ($document.SelectNodes("/Project/PropertyGroup/$name").Count -ne 1) {
            throw "Expected exactly one $name in version.props."
        }
    }
    return ,$document
}

function Get-PureRdpCurrentVersion([string]$VersionFile) {
    $document = Read-PureRdpVersionDocument $VersionFile
    return ConvertTo-PureRdpVersion $document.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
}

function Get-PureRdpPackageVersion {
    param(
        [Parameter(Mandatory)][string]$VersionFile,
        [string]$ExplicitVersion,
        [Nullable[DateTimeOffset]]$Now
    )
    # Release tags and deliberately pinned builds must retain the requested version.
    if (-not [string]::IsNullOrWhiteSpace($ExplicitVersion)) {
        return ConvertTo-PureRdpVersion $ExplicitVersion
    }
    $path = (Resolve-Path -LiteralPath $VersionFile -ErrorAction Stop).Path
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [Convert]::ToHexString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToUpperInvariant()))) }
    finally { $hash.Dispose() }
    $mutex = [Threading.Mutex]::new($false, "Local\PureRDP.PackageVersion.$key")
    $held = $false
    $temporary = $null
    try {
        try { $held = $mutex.WaitOne(30000) }
        catch [Threading.AbandonedMutexException] { $held = $true }
        if (-not $held) { throw 'Another package build is updating the version; retry later.' }
        $document = Read-PureRdpVersionDocument $path
        $versionNode = $document.SelectSingleNode('/Project/PropertyGroup/Version')
        $dateNode = $document.SelectSingleNode('/Project/PropertyGroup/LastPackageVersionDate')
        $version = ConvertTo-PureRdpVersion $versionNode.InnerText
        $instant = if ($null -eq $Now) { [DateTimeOffset]::UtcNow } else { [DateTimeOffset]$Now }
        $today = $instant.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
        if (-not [string]::IsNullOrWhiteSpace($dateNode.InnerText)) {
            $savedDate = [datetime]::MinValue
            if (-not [datetime]::TryParseExact($dateNode.InnerText, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::None, [ref]$savedDate)) { throw 'Invalid LastPackageVersionDate; check version.props.' }
            if ([string]::CompareOrdinal($dateNode.InnerText, $today) -gt 0) { throw 'Package date is in the future; check the clock before building.' }
            if ($dateNode.InnerText -eq $today) { return $version }
        }
        $parts = @($version.Split('.') | ForEach-Object { [int]$_ })
        if ($parts[2] -ge 65534) { throw 'Patch version limit reached; explicitly choose a new major/minor version.' }
        if ($parts[2] -eq 9) {
            if ($parts[1] -ge 65534) { throw 'Minor version limit reached; explicitly choose a new major version.' }
            $parts[1]++
            $parts[2] = 0
        } else { $parts[2]++ }
        $version = $parts -join '.'
        $fileVersion = if ($parts.Count -eq 3) { "$version.0" } else { $version }
        $versionNode.InnerText = $version
        $document.SelectSingleNode('/Project/PropertyGroup/FileVersion').InnerText = $fileVersion
        $dateNode.InnerText = $today
        $temporary = $path + '.' + [guid]::NewGuid().ToString('N') + '.tmp'
        $settings = [Xml.XmlWriterSettings]::new()
        $settings.Encoding = [Text.UTF8Encoding]::new($false)
        $settings.OmitXmlDeclaration = $true
        $settings.NewLineHandling = [Xml.NewLineHandling]::None
        $writer = [Xml.XmlWriter]::Create($temporary, $settings)
        try { $document.Save($writer) } finally { $writer.Dispose() }
        # Reserve before compilation: failed builds retry the same number that day.
        [IO.File]::Replace($temporary, $path, [NullString]::Value)
        return $version
    }
    finally {
        if ($temporary -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}
