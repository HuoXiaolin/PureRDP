param(
    [string]$Dotnet = '',
    [string]$Version = '',
    [string]$ReleaseDirectory = 'D:\Agant\Github_Releases',
    [string]$SourceDirectory = 'D:\Agant\Github_Source'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. $PSScriptRoot/_version.ps1
. $PSScriptRoot/_source-export.ps1
$Version = Get-PureRdpPackageVersion -VersionFile (Join-Path $repoRoot 'Build/targets/version.props') -ExplicitVersion $Version
Write-Host "Package version: $Version (daily patch policy uses Beijing time)."
$stageDirectory = Join-Path $repoRoot "outputs\$Version-independent-preview"
if ([string]::IsNullOrWhiteSpace($Dotnet)) {
    $localDotnet = 'D:\Agant\toolchain\dotnet\dotnet.exe'
    $Dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
}
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
. $PSScriptRoot/_nuget-cache.ps1
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$versionParts = $Version.Split('.')
while ($versionParts.Count -lt 4) { $versionParts += '0' }
$fileVersion = $versionParts[0..3] -join '.'
# Framework-dependent only. Deliver ordinary folders, never ZIP/runtime packages.
# Refresh assets so package paths follow the shared cache after migration.
& $Dotnet restore (Join-Path $repoRoot 'PureRDP\PureRDP.csproj') -r win-x64 `
    -p:SelfContained=false -p:PublishSingleFile=true --ignore-failed-sources
if ($LASTEXITCODE -ne 0) { throw 'Restore from the shared NuGet cache failed.' }
& $Dotnet publish (Join-Path $repoRoot 'PureRDP\PureRDP.csproj') -c Release -f net10.0-windows -r win-x64 --self-contained false --no-restore `
    -p:PublishSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:RunAnalyzers=true -p:RunAnalyzersDuringBuild=true -p:TreatWarningsAsErrors=true `
    "-p:Version=$Version" "-p:FileVersion=$fileVersion" -o $stageDirectory
if ($LASTEXITCODE -ne 0) { throw 'Framework-dependent publish failed.' }
$publishedInfo = (Get-Item -LiteralPath (Join-Path $stageDirectory 'PureRDP.exe')).VersionInfo
if ($publishedInfo.FileVersion -ne $fileVersion -or $publishedInfo.ProductVersion -ne $Version) {
    throw "Published EXE version mismatch: expected $Version / $fileVersion; got $($publishedInfo.ProductVersion) / $($publishedInfo.FileVersion)."
}
Write-Host "Verified EXE product version $Version; file version $fileVersion."
$releaseDocs = @('LICENSE', 'README.md', 'README.zh-CN.md', 'CHANGELOG.md', 'CHANGELOG_HISTORY.md')
Copy-Item -LiteralPath @($releaseDocs | ForEach-Object { Join-Path $repoRoot $_ }) -Destination $stageDirectory -Force
$publishId = [Guid]::NewGuid().ToString('N')
$exportStage = Join-Path $repoRoot "outputs\.folder-publish-$publishId"
$releaseFolder = Join-Path $exportStage 'release\PureRDP'
$sourceFolder = Join-Path $exportStage 'source\PureRDP'
# Verify complete fresh exports before replacing the fixed latest directories.
foreach ($directory in @($releaseFolder, $sourceFolder)) {
    if (Test-Path -LiteralPath $directory) { throw "Output folder already exists: $directory" }
    [void](New-Item -ItemType Directory -Path $directory -Force)
}
$stageFiles = @('PureRDP.exe') + $releaseDocs
$sourceFiles = @()
foreach ($folder in @('PureRDP', 'PureRDP.Installer', 'Build', 'docs', '.github', '.vscode', '.config')) {
    $root = Join-Path $repoRoot $folder
    if (Test-Path -LiteralPath $root) {
        $sourceFiles += Get-ChildItem -LiteralPath $root -File -Recurse |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.git|TestResults)[\\/]' }
    }
}
foreach ($file in @('LICENSE', 'README.md', 'README.zh-CN.md', 'CHANGELOG.md', 'CHANGELOG_HISTORY.md', '.gitignore', 'PureRDP.slnx', 'PureRDP.sln.DotSettings')) {
    $path = Join-Path $repoRoot $file
    if (Test-Path -LiteralPath $path) { $sourceFiles += Get-Item -LiteralPath $path }
}
function Copy-RelativeFile([string]$path, [string]$relative, [string]$root) {
    $resolvedRoot = [IO.Path]::GetFullPath($root)
    $destination = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not $destination.StartsWith($resolvedRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw "Invalid output path: $relative" }
    [void](New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force)
    Copy-Item -LiteralPath $path -Destination $destination
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash) {
        throw "Copied file hash mismatch: $relative"
    }
}
$compatibilityDoc = 'docs\RDP-compatibility.md'
Copy-RelativeFile (Join-Path $repoRoot $compatibilityDoc) $compatibilityDoc $stageDirectory
$stageFiles += $compatibilityDoc
foreach ($file in $stageFiles) {
    Copy-RelativeFile (Join-Path $stageDirectory $file) $file $releaseFolder
    Copy-RelativeFile (Join-Path $stageDirectory $file) "outputs\$Version-independent-preview\$file" $sourceFolder
}
# Keep relative README image links valid in every delivered README location.
# The source root gets the same docs through the source-file list below.
$screenshots = Join-Path $repoRoot 'docs\screenshots'
if (Test-Path -LiteralPath $screenshots) {
    foreach ($asset in Get-ChildItem -LiteralPath $screenshots -File) {
        $relative = "docs\screenshots\$($asset.Name)"
        Copy-RelativeFile $asset.FullName $relative $stageDirectory
        Copy-RelativeFile $asset.FullName $relative $releaseFolder
        Copy-RelativeFile $asset.FullName "outputs\$Version-independent-preview\$relative" $sourceFolder
    }
}
# Never export caches, historical packages or unrelated user work folders.
foreach ($file in $sourceFiles | Sort-Object FullName -Unique) {
    Copy-RelativeFile $file.FullName $file.FullName.Substring($repoRoot.Length + 1) $sourceFolder
}
$backupRoot = Join-Path $repoRoot "outputs\release-history\$publishId"
$script:publishBlocked = $false
function Publish-Latest([string]$staging, [string]$outputRoot, [string]$kind) {
    $resolvedRoot = [IO.Path]::GetFullPath($outputRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    [void](New-Item -ItemType Directory -Path $resolvedRoot -Force)
    $latest = [IO.Path]::GetFullPath((Join-Path $resolvedRoot 'PureRDP'))
    if ([IO.Path]::GetDirectoryName($latest) -ne $resolvedRoot -or [IO.Path]::GetFileName($latest) -ne 'PureRDP') {
        throw "Invalid latest directory: $latest"
    }
    $backup = [IO.Path]::GetFullPath((Join-Path $backupRoot $kind))
    if (-not $backup.StartsWith($repoRoot + '\outputs\release-history\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Invalid backup directory: $backup"
    }
    if ($kind -eq 'source') {
        Publish-PureRdpSourceFiles -Staging $staging -Destination $latest -Backup (Join-Path $backup 'PureRDP')
        return
    }
    if (Test-Path -LiteralPath $latest) {
        [void](New-Item -ItemType Directory -Path $backup -Force)
        try { Move-Item -LiteralPath $latest -Destination (Join-Path $backup 'PureRDP') -ErrorAction Stop }
        catch [IO.IOException] {
            $script:publishBlocked = $true
            Write-Warning "Latest $kind folder is in use and was not replaced: $latest. Verified new files remain at $staging. Exit PureRDP from the tray and rerun publication."
            return
        }
    }
    try { Move-Item -LiteralPath $staging -Destination $latest }
    catch {
        $previous = Join-Path $backup 'PureRDP'
        if ((Test-Path -LiteralPath $previous) -and -not (Test-Path -LiteralPath $latest)) {
            Move-Item -LiteralPath $previous -Destination $latest
        }
        throw
    }
    # Archive only this project's old dated deliveries. Leave other projects
    # untouched and make removal from the public output roots recoverable.
    foreach ($old in Get-ChildItem -LiteralPath $resolvedRoot -Directory |
        Where-Object { $_.Name -match '^PureRDP-\d+\.\d+\.\d+-(win-x64-no-runtime|source)-\d{8}-\d{6}$' }) {
        $oldPath = [IO.Path]::GetFullPath($old.FullName)
        if ([IO.Path]::GetDirectoryName($oldPath) -ne $resolvedRoot -or
            ($old.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Invalid old output directory: $oldPath" }
        [void](New-Item -ItemType Directory -Path $backup -Force)
        try { Move-Item -LiteralPath $oldPath -Destination (Join-Path $backup $old.Name) -ErrorAction Stop }
        catch [IO.IOException] {
            Write-Warning "Old PureRDP output is in use and remains at $oldPath. Exit that version from the tray, then rerun publication to archive it."
        }
    }
    Write-Host "Latest $kind folder: $latest"
}
Publish-Latest $releaseFolder $ReleaseDirectory 'release'
Publish-Latest $sourceFolder $SourceDirectory 'source'
Write-Host "Previous PureRDP outputs, if any, are recoverable in: $backupRoot"
Write-Host 'All copied files passed SHA256 verification. No ZIP or self-contained package was generated.'
if ($script:publishBlocked) { throw 'Publication is incomplete: an active PureRDP output folder could not be replaced. No process was terminated.' }


