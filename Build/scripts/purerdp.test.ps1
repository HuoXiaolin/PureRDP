param([string]$version = '2.0.0')
$ErrorActionPreference = 'Stop'
. $PSScriptRoot/_core.ps1
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
Push-Location $repo
try {
    & "$PSScriptRoot/purerdp.build.ps1" -version $version
    if ($LASTEXITCODE -ne 0) { throw 'Application build failed.' }
    $reports = Join-Path $repo 'outputs\ci-tests\TestResults'
    New-Item -ItemType Directory -Path $reports -Force | Out-Null
    $results = @()
    foreach ($name in @('UpdateCheck', 'RdpHistory', 'ShortcutFirstPress', 'WindowMinimize', 'ShutdownDisconnect', 'StatusRefresh', 'UiDesign', 'Favorites')) {
        $watch = [Diagnostics.Stopwatch]::StartNew()
        $project = Join-Path $repo "Build\tests\$name\$name.csproj"
        $output = @(& $script:DotnetCommand run --project $project -c Release --no-launch-profile 2>&1 | ForEach-Object { "$_" })
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
