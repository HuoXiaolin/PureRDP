# Update files in place: the destination directory and its .git identity never move.
function Publish-PureRdpSourceFiles([string]$Staging, [string]$Destination, [string]$Backup) {
    $ErrorActionPreference = 'Stop'
    $stageRoot = [IO.Path]::GetFullPath($Staging).TrimEnd('\')
    $targetRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
    $backupRoot = [IO.Path]::GetFullPath($Backup).TrimEnd('\')
    $roots = @($stageRoot, $targetRoot, $backupRoot)
    foreach ($a in $roots) {
        foreach ($b in $roots) {
            if ($a -ne $b -and $a.StartsWith($b + '\', [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Source export roots must not contain one another.'
            }
        }
    }
    if (($roots | Sort-Object -Unique).Count -ne 3) { throw 'Source export roots must be distinct.' }
    function Assert-NoExportLink([string]$Path) {
        $cursor = $Path
        while ($cursor) {
            if (Test-Path -LiteralPath $cursor) {
                if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                    throw "Source export refuses reparse points: $cursor"
                }
            }
            $cursor = [IO.Path]::GetDirectoryName($cursor)
        }
    }
    foreach ($root in $roots) { Assert-NoExportLink $root }
    # Traverse explicitly, without following directory links.
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($stageRoot)
    $files = @()
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.Name -eq '.git' -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Invalid staged source entry: $($item.FullName)"
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) } else { $files += $item }
        }
    }
    $plan = foreach ($file in $files) {
        $relative = $file.FullName.Substring($stageRoot.Length + 1)
        $target = Join-Path $targetRoot $relative
        $saved = Join-Path $backupRoot $relative
        Assert-NoExportLink $target
        Assert-NoExportLink $saved
        if ((Test-Path -LiteralPath $target) -and (Get-Item -LiteralPath $target -Force).PSIsContainer) {
            throw "Source export file conflicts with directory: $target"
        }
        if (Test-Path -LiteralPath $saved) { throw "Backup already exists: $saved" }
        [pscustomobject]@{ Source=$file.FullName; Target=$target; Saved=$saved; Existed=(Test-Path -LiteralPath $target) }
    }
    # Back up every replaced file before changing any destination file.
    foreach ($entry in $plan) {
        if ($entry.Existed) {
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Saved) -Force)
            Copy-Item -LiteralPath $entry.Target -Destination $entry.Saved
            if ((Get-FileHash -LiteralPath $entry.Target).Hash -ne (Get-FileHash -LiteralPath $entry.Saved).Hash) {
                throw "Source backup hash mismatch: $($entry.Target)"
            }
        }
    }
    $touched = [Collections.Generic.List[object]]::new()
    try {
        foreach ($entry in $plan) {
            [void](New-Item -ItemType Directory -Path (Split-Path -Parent $entry.Target) -Force)
            $touched.Add($entry)
            Copy-Item -LiteralPath $entry.Source -Destination $entry.Target -Force
            if ((Get-FileHash -LiteralPath $entry.Source).Hash -ne (Get-FileHash -LiteralPath $entry.Target).Hash) {
                throw "Source export hash mismatch: $($entry.Target)"
            }
        }
    } catch {
        $failure = $_
        foreach ($entry in $touched) {
            try {
                if ($entry.Existed) { Copy-Item -LiteralPath $entry.Saved -Destination $entry.Target -Force }
                else { Remove-Item -LiteralPath $entry.Target -Force }
            } catch { Write-Warning "Could not roll back $($entry.Target); backup: $($entry.Saved)" }
        }
        throw $failure
    }
    Write-Host "Source files updated in place; .git and unrelated files preserved: $targetRoot"
}
