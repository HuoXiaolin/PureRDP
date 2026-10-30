param(
    [ValidateRange(0, 300)][int]$WatchSeconds = 60,
    [string]$FilePath = ''
)
# Windows PowerShell 5.1 / PowerShell 7. Read-only diagnosis: never copies or
# prints passwords, usernames, endpoints, command lines or session tickets.
$ErrorActionPreference = 'Stop'
$seen = @{}
$seenProcesses = @{}
function Show-RdpMetadata([string]$Path, [string]$Source = 'explicit file') {
    try {
        if (-not [IO.File]::Exists($Path)) { return }
        $item = Get-Item -LiteralPath $Path
        if ($item.Length -gt 1MB -or $item.Extension -in @('.exe', '.dll', '.jar')) { return }
        $lines = [IO.File]::ReadAllLines($item.FullName, [Text.Encoding]::Unicode)
        if (-not ($lines -match '^\s*full address\s*:[sS]:')) {
            $lines = [IO.File]::ReadAllLines($item.FullName, [Text.Encoding]::UTF8)
        }
        if (-not ($lines -match '^\s*full address\s*:[sS]:')) { return }
        $key = $Source + '|' + $item.FullName + '|' + $item.LastWriteTimeUtc.Ticks + '|' + $item.Length
        if ($seen.ContainsKey($key)) { return }
        $seen[$key] = $true
        $fields = @{}
        foreach ($line in $lines) {
            $parts = $line -split ':', 3
            if ($parts.Count -eq 3) { $fields[$parts[0].Trim().ToLowerInvariant()] = $parts[2] }
        }
        Write-Output ''
        Write-Output ('RDP file: ' + $item.FullName)
        Write-Output ('Capture source: ' + $Source)
        # Fixed names only: vendor-defined field names can themselves contain
        # identifiers or tickets. Never enumerate arbitrary names or values.
        foreach ($name in @('username', 'domain', 'password 51', 'password', 'loadbalanceinfo', 'pcb', 'gatewayhostname', 'alternate shell', 'shell working directory', 'remoteapplicationprogram', 'remoteapplicationcmdline', 'remoteapplicationname', 'gatewayaccesstoken', 'kdcproxyname')) {
            $present = $fields.ContainsKey($name) -and -not [string]::IsNullOrWhiteSpace($fields[$name])
            Write-Output ($name + ' present: ' + $present)
        }
        $knownNames = @('full address', 'username', 'domain', 'password 51', 'password', 'loadbalanceinfo', 'pcb', 'gatewayhostname', 'alternate shell', 'shell working directory', 'remoteapplicationprogram', 'remoteapplicationcmdline', 'remoteapplicationname', 'gatewayaccesstoken', 'kdcproxyname')
        $otherStrings = @($lines | Where-Object {
            $segments = $_ -split ':', 3
            $segments.Count -eq 3 -and $segments[1].Trim() -in @('s', 'b') -and
                $segments[0].Trim().ToLowerInvariant() -notin $knownNames -and
                -not [string]::IsNullOrWhiteSpace($segments[2])
        }).Count
        Write-Output ('Other nonempty string/binary field count: ' + $otherStrings)
        foreach ($name in @('enablecredsspsupport', 'prompt for credentials', 'negotiate security layer')) {
            if ($fields.ContainsKey($name) -and $fields[$name] -match '^[012]$') {
                Write-Output ($name + ': ' + $fields[$name])
            }
        }
    }
    catch [IO.IOException] { } # A subsequent Changed event can retry a locked file.
    catch [UnauthorizedAccessException] { }
}

if ($FilePath) { Show-RdpMetadata $FilePath; return }
Write-Output 'Read-only RDP handoff inspection v2. Password and ticket values are never printed.'
Write-Output 'Now launch one connection from your operations assistant. Keep the native RDP window open.'

if (-not ('PureRdpHandoffArgv' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PureRdpHandoffArgv {
    [DllImport("shell32.dll", CharSet=CharSet.Unicode)]
    public static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr pointer);
}
'@
}
function Inspect-RunningRdp {
    try {
        $processes = Get-CimInstance Win32_Process -Filter "Name='mstsc.exe' OR Name='PureRDP.exe'" -ErrorAction Stop
        foreach ($process in $processes) {
            $processKey = [string]$process.ProcessId + '|' + [string]$process.CreationDate
            if (-not $seenProcesses.ContainsKey($processKey)) {
                $seenProcesses[$processKey] = $true
                # Only executable names, never command lines or account names.
                $parent = Get-CimInstance Win32_Process -Filter ('ProcessId=' + [uint32]$process.ParentProcessId) -ErrorAction SilentlyContinue
                Write-Output ('RDP process: ' + $process.Name + '; parent: ' + $(if ($parent) { $parent.Name } else { 'unavailable' }))
            }
            if (-not $process.CommandLine) { continue }
            $count = 0
            $pointer = [PureRdpHandoffArgv]::CommandLineToArgvW($process.CommandLine, [ref]$count)
            if ($pointer -eq [IntPtr]::Zero) { continue }
            try {
                for ($index = 1; $index -lt $count; $index++) {
                    $argument = [Runtime.InteropServices.Marshal]::PtrToStringUni(
                        [Runtime.InteropServices.Marshal]::ReadIntPtr($pointer, $index * [IntPtr]::Size))
                    if ($argument.StartsWith('--rdp-file=')) { $argument = $argument.Substring(11) }
                    if ([IO.File]::Exists($argument)) { Show-RdpMetadata $argument 'running RDP argument' }
                }
            }
            finally { [void][PureRdpHandoffArgv]::LocalFree($pointer) }
        }
    }
    catch { Write-Warning 'Process inspection unavailable; temporary-file monitoring will continue.' }
}

$queue = New-Object 'System.Collections.Concurrent.ConcurrentQueue[string]'
$watchers = @()
$subscriptions = @()
try {
    foreach ($root in @($env:TEMP, 'C:\ORAssistant') | Select-Object -Unique) {
        if (-not [IO.Directory]::Exists($root)) { continue }
        $watcher = New-Object IO.FileSystemWatcher $root, '*'
        $watcher.IncludeSubdirectories = $true
        $watcher.NotifyFilter = [IO.NotifyFilters]'FileName, LastWrite, Size'
        foreach ($eventName in @('Created', 'Changed', 'Renamed')) {
            $id = 'PureRdpHandoff.' + [Guid]::NewGuid().ToString('N')
            $job = Register-ObjectEvent -InputObject $watcher -EventName $eventName -SourceIdentifier $id -MessageData $queue -Action {
                $Event.MessageData.Enqueue($Event.SourceEventArgs.FullPath)
            }
            $subscriptions += [pscustomobject]@{ Id = $id; Job = $job }
        }
        $watcher.EnableRaisingEvents = $true
        $watchers += $watcher
        Get-ChildItem -LiteralPath $root -Filter '*.rdp' -Recurse -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 10 |
            ForEach-Object { Show-RdpMetadata $_.FullName 'existing file (may be unrelated history)' }
    }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $lastProcessScan = -10
    do {
        if ($watch.Elapsed.TotalSeconds - $lastProcessScan -ge 2) {
            Inspect-RunningRdp
            $lastProcessScan = $watch.Elapsed.TotalSeconds
        }
        $candidate = $null
        while ($queue.TryDequeue([ref]$candidate)) { Show-RdpMetadata $candidate 'created or changed during inspection' }
        if ($watch.Elapsed.TotalSeconds -ge $WatchSeconds) { break }
        Start-Sleep -Milliseconds 200
    } while ($true)
    Write-Output ''
    if ($seen.Count -eq 0) { Write-Output 'No RDP file captured. Disable the MSTSC proxy temporarily, launch native RDP again, and rerun this script.' }
    else { Write-Output 'Inspection complete. Share only these metadata lines, not the original RDP file or its password/ticket values.' }
}
finally {
    foreach ($watcher in $watchers) { $watcher.Dispose() }
    foreach ($subscription in $subscriptions) {
        Unregister-Event -SourceIdentifier $subscription.Id -ErrorAction SilentlyContinue
        Remove-Job -Job $subscription.Job -Force -ErrorAction SilentlyContinue
    }
}
