param([switch]$Apply)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path
$root = [IO.Path]::GetFullPath((Join-Path $repo 'outputs'))
. "$PSScriptRoot/_version.ps1"
$current = Get-PureRdpCurrentVersion (Join-Path $repo 'Build/targets/version.props')
$currentPackage = "$current-independent-preview"
$exe = Join-Path $root "$currentPackage/PureRDP.exe"
$delivery = 'D:\Agant\Github_Releases\PureRDP\PureRDP.exe'
if (-not (Test-Path -LiteralPath $exe) -or -not (Test-Path -LiteralPath $delivery) -or
    (Get-FileHash -LiteralPath $exe).Hash -ne (Get-FileHash -LiteralPath $delivery).Hash) {
    throw 'Current build and delivered EXE must exist and match before cleanup.'
}
function Get-SafeFiles([string]$Path) {
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Path)
    $files = [Collections.Generic.List[object]]::new()
    while ($pending.Count) {
        $item = Get-Item -LiteralPath $pending.Pop() -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Protected link: $($item.FullName)" }
        if ($item.Name -eq '.git' -or $item.Extension -eq '.bundle') { throw "Protected Git history: $($item.FullName)" }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) { $pending.Push($child.FullName) }
        } else { $files.Add($item) }
    }
    return $files.ToArray()
}
$candidates = @(Get-ChildItem -LiteralPath $root -Force | Where-Object {
    $_.Name -ne $currentPackage -and (
        $_.Name -match '^\.folder-publish-[a-f0-9]{32}$' -or
        $_.Name -match '^\d+\.\d+\.\d+(\.\d+)?-independent-preview$' -or
        $_.Name -match '^ci-clean-[a-f0-9]{32}$' -or
        $_.Name -in @('daily-version-tests', 'documentation-screenshots', 'dpi-validation',
            'dpi-validation-tools', 'migrate-nuget.ps1', 'rebuild-git-history.ps1') -or
        $_.Name -match '^git-rebuild-[a-f0-9]{32}$'
    )
})
$history = Join-Path $root 'release-history'
if (Test-Path -LiteralPath $history) {
    $archives = @(Get-ChildItem -LiteralPath $history -Directory -Force | Sort-Object LastWriteTime -Descending)
    # Keep the latest rollback, even when older ones are safe to remove.
    $candidates += @($archives | Select-Object -Skip 1)
}
$plan = @()
foreach ($candidate in $candidates) {
    $path = [IO.Path]::GetFullPath($candidate.FullName)
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Out of bounds: $path" }
    try { $files = @(Get-SafeFiles $path) }
    catch { Write-Warning "Retained $path : $_"; continue }
    $plan += [pscustomobject]@{ Path=$path; Files=$files.Count; Bytes=($files | Measure-Object Length -Sum).Sum }
}
$plan | Select-Object Path, Files, @{n='MiB';e={[math]::Round($_.Bytes/1MB,2)}} | Format-Table -AutoSize
if (-not $Apply) { Write-Host 'Preview only. Use -Apply to remove the listed generated files.'; return }
$removedBytes = 0L
foreach ($entry in $plan) {
    # Revalidate immediately before recursive deletion; never follow links.
    [void](Get-SafeFiles $entry.Path)
    if ([IO.Path]::GetFullPath($entry.Path) -eq $root -or
        -not $entry.Path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe cleanup target.' }
    try {
        Remove-Item -LiteralPath $entry.Path -Recurse -Force -ErrorAction Stop
        $removedBytes += [long]$entry.Bytes
    } catch { Write-Warning "Cleanup incomplete for $($entry.Path); no processes were stopped: $_" }
}
Write-Host "Removed generated files: $([math]::Round($removedBytes/1MB,2)) MiB. Deletion is permanent; kept current build, evidence, latest rollback and protected Git histories."
