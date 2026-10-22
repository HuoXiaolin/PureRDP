param(
    [string]$version = '', [string]$Dotnet = '',
    [ValidateSet('All', 'UpdateCheck', 'RdpHistory', 'ShortcutFirstPress', 'WindowMinimize', 'ShutdownDisconnect', 'StatusRefresh', 'UiDesign', 'Favorites', 'DailyVersion')]
    [string]$Suite = 'All',
    [switch]$DesktopIsolated
)
$ErrorActionPreference = 'Stop'
. $PSScriptRoot/_core.ps1
if (-not [string]::IsNullOrWhiteSpace($Dotnet)) { $script:DotnetCommand = $Dotnet }
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not ('PureRdpTestDesktop' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot 'TestDesktop.cs') }
if (-not $DesktopIsolated) {
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath, '-DesktopIsolated', '-Dotnet', $script:DotnetCommand, '-Suite', $Suite)
    if ($version) { $arguments += @('-version', $version) }
    $exitCode = [PureRdpTestDesktop]::Run((Join-Path $PSHOME 'pwsh.exe'), $arguments, $repo)
    if ($exitCode -ne 0) { throw "Isolated regressions failed (exit $exitCode); see test output and TestResults." }
    $global:LASTEXITCODE = 0
    return
}
if (-not [PureRdpTestDesktop]::CurrentDesktop().StartsWith('PureRDPTests_', [StringComparison]::Ordinal)) {
    throw 'GUI tests must run on an isolated desktop; refusing visible-window fallback.'
}
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Push-Location $repo
try {
    & "$PSScriptRoot/purerdp.build.ps1" -version $version -Dotnet $script:DotnetCommand
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    $reports = if ($Suite -eq 'All') { Join-Path $repo 'outputs\ci-tests\TestResults' }
        else { Join-Path $repo "outputs\ci-tests\SelectedSuite-$Suite" }
    New-Item -ItemType Directory -Path $reports -Force | Out-Null
    $results = @()
    foreach ($name in @('UpdateCheck', 'RdpHistory', 'ShortcutFirstPress', 'WindowMinimize', 'ShutdownDisconnect', 'StatusRefresh', 'UiDesign', 'Favorites', 'DailyVersion')) {
        if ($Suite -ne 'All' -and $name -ne $Suite) { continue }
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $project = Join-Path $repo "Build\tests\$name\$name.csproj"
        $output = @(if ($name -eq 'DailyVersion') {
            & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File (Join-Path $repo 'Build/tests/DailyVersion/DailyVersion.ps1') 2>&1 | ForEach-Object { "$_" }
        } else {
            & $script:DotnetCommand run --project $project -c Release --no-launch-profile 2>&1 | ForEach-Object { "$_" }
        })
        $code = $LASTEXITCODE
        $watch.Stop()
        $output | ForEach-Object { Write-Host $_ }
        [IO.File]::WriteAllLines((Join-Path $reports "$name.log"), [string[]]$output)
        $results += [pscustomobject]@{ Name=$name; ExitCode=$code; Seconds=$watch.Elapsed.TotalSeconds; Output=($output -join "`n") }
    }
    $writer = [Xml.XmlWriter]::Create((Join-Path $reports 'regression-results.xml'))
    try {
        $writer.WriteStartDocument()
        $writer.WriteStartElement('testsuite')
        $writer.WriteAttributeString('name', 'PureRDP offline regressions')
        $writer.WriteAttributeString('tests', "$($results.Count)")
        $writer.WriteAttributeString('failures', "$(@($results | Where-Object ExitCode -ne 0).Count)")
        foreach ($result in $results) {
            $writer.WriteStartElement('testcase')
            $writer.WriteAttributeString('name', $result.Name)
            $writer.WriteAttributeString('time', $result.Seconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture))
            if ($result.ExitCode -ne 0) { $writer.WriteElementString('failure', $result.Output) }
            $writer.WriteElementString('system-out', $result.Output)
            $writer.WriteEndElement()
        }
        $writer.WriteEndElement()
        $writer.WriteEndDocument()
    } finally { $writer.Dispose() }
    if (@($results | Where-Object ExitCode -ne 0).Count -ne 0) { throw 'One or more offline regression suites failed; see TestResults.' }
    Write-Host "PASS: $($results.Count) regression suites; reports: $reports"
} finally { Pop-Location }
