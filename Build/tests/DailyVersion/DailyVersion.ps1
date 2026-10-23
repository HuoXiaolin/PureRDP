$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$helper = Join-Path $repo 'Build/scripts/_version.ps1'
. $helper
$production = Join-Path $repo 'Build/targets/version.props'
$productionHash = (Get-FileHash -LiteralPath $production).Hash
$testRoot = Join-Path $repo ('outputs/daily-version-tests/' + [guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $testRoot)
$script:checks = 0
function Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Fixture([string]$Name, [string]$Version = '1.0.0', [string]$Date = '') {
    $path = Join-Path $testRoot ($Name + '.props')
    $text = '<Project><PropertyGroup><Version>{0}</Version><FileVersion>1.0.0.0</FileVersion><LastPackageVersionDate>{1}</LastPackageVersionDate><Authors>测试作者</Authors></PropertyGroup></Project>' -f $Version, $Date
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
    return $path
}
function Expect-UnchangedFailure([string]$Path, [scriptblock]$Action) {
    $before = (Get-FileHash -LiteralPath $Path).Hash
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Check $failed 'Expected validation failure.'
    Check ((Get-FileHash -LiteralPath $Path).Hash -eq $before) 'Failed validation must not change version state.'
}
$path = Fixture 'daily'
$before = (Get-FileHash -LiteralPath $path).Hash
Check ((Get-PureRdpCurrentVersion $path) -eq '1.0.0') 'Normal builds read the current version.'
Check ((Get-FileHash -LiteralPath $path).Hash -eq $before) 'Read/test builds must not reserve a daily version.'
Check ((Get-PureRdpPackageVersion $path -Now '2026-10-19T15:59:59Z') -eq '1.0.1') 'First daily package increments the third component.'
[xml]$saved = Get-Content -LiteralPath $path -Raw -Encoding utf8
Check ($saved.Project.PropertyGroup.LastPackageVersionDate -eq '2026-10-19') 'Use Beijing date before midnight.'
Check ($saved.Project.PropertyGroup.FileVersion -eq '1.0.1.0') 'Windows file version follows product version.'
Check ($saved.Project.PropertyGroup.Authors -eq '测试作者') 'Preserve unrelated metadata and UTF-8.'
$before = (Get-FileHash -LiteralPath $path).Hash
Check ((Get-PureRdpPackageVersion $path -Now '2026-10-19T23:59:59+08:00') -eq '1.0.1') 'Same day retry keeps its allocated version.'
Check ((Get-FileHash -LiteralPath $path).Hash -eq $before) 'Same day does not rewrite state.'
Check ((Get-PureRdpPackageVersion $path -Now '2026-10-19T16:00:00Z') -eq '1.0.2') 'Beijing midnight increments once, independent of UTC date.'
Check ((Get-PureRdpPackageVersion $path -Now '2026-10-20T15:59:59Z') -eq '1.0.2') 'All later builds on that Beijing day keep the version.'
Check ((Get-PureRdpPackageVersion $path -Now '2026-10-20T16:00:00Z') -eq '1.0.3') 'Next Beijing day increments once.'
Check ((Get-PureRdpPackageVersion $path -Now '2026-11-10T00:00:00Z') -eq '1.0.4') 'Skipped days do not cause catch-up increments.'
$before = (Get-FileHash -LiteralPath $path).Hash
Check ((Get-PureRdpPackageVersion $path -ExplicitVersion 'v3.4.5') -eq '3.4.5') 'Explicit release tag remains fixed.'
Check ((Get-FileHash -LiteralPath $path).Hash -eq $before) 'Release tags do not consume or change local daily state.'
Check ((Get-PureRdpPackageVersion $path -ExplicitVersion 'V03.04.005.6') -eq '3.4.5.6') 'Normalize pinned four-part versions.'
Expect-UnchangedFailure $path { Get-PureRdpPackageVersion $path -ExplicitVersion '1.0' }
Expect-UnchangedFailure $path { Get-PureRdpPackageVersion $path -ExplicitVersion '1.0.1-beta' }
Expect-UnchangedFailure $path { Get-PureRdpPackageVersion $path -Now '2026-10-20T00:00:00Z' }
$four = Fixture 'four' '1.2.3.7'
Check ((Get-PureRdpPackageVersion $four -Now '2026-10-20T00:00:00Z') -eq '1.2.4.7') 'Four-part versions still increment the third component.'
[xml]$saved = Get-Content -LiteralPath $four -Raw
Check ($saved.Project.PropertyGroup.FileVersion -eq '1.2.4.7') 'Preserve explicit fourth component.'
$year = Fixture 'year' '2.9.5' '2026-12-31'
Check ((Get-PureRdpPackageVersion $year -Now '2027-01-01T00:00:00+08:00') -eq '2.9.6') 'Year changes do not reset the version.'
$carry = Fixture 'carry' '2.0.9' '2026-10-22'
Check ((Get-PureRdpPackageVersion $carry -Now '2026-10-23T00:00:00+08:00') -eq '2.1.0') 'Patch 9 carries into minor, not 2.0.10.'
Check ((Get-PureRdpPackageVersion $carry -Now '2026-10-23T12:00:00+08:00') -eq '2.1.0') 'Carry counts as the only daily increment.'
$minorCarry = Fixture 'minor-carry' '2.9.9'
Check ((Get-PureRdpPackageVersion $minorCarry -Now '2026-10-23T00:00:00+08:00') -eq '2.10.0') 'Carry must not change the major version.'
$badDate = Fixture 'bad-date' '1.0.0' 'invalid'
Expect-UnchangedFailure $badDate { Get-PureRdpPackageVersion $badDate -Now '2026-10-20T00:00:00Z' }
$limit = Fixture 'limit' '1.0.65534'
Expect-UnchangedFailure $limit { Get-PureRdpPackageVersion $limit -Now '2026-10-20T00:00:00Z' }
$invalid = Fixture 'invalid' '1.0.99999999999999999999'
Expect-UnchangedFailure $invalid { Get-PureRdpPackageVersion $invalid -Now '2026-10-20T00:00:00Z' }
$missing = Join-Path $testRoot 'missing.props'
$failed = $false
try { Get-PureRdpPackageVersion $missing | Out-Null } catch { $failed = $true }
Check $failed 'Missing state must fail without resetting to an old version.'
$current = Fixture 'current-date'
[void](Get-PureRdpPackageVersion $current)
[xml]$saved = Get-Content -LiteralPath $current -Raw
Check ($saved.Project.PropertyGroup.LastPackageVersionDate -eq [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(8)).ToString('yyyy-MM-dd')) 'Default clock uses Beijing date.'
$concurrent = Fixture 'concurrent'
$jobs = @()
try {
    $jobs = @(1..4 | ForEach-Object {
        Start-Job -ScriptBlock {
            param($HelperPath, $StatePath)
            $ErrorActionPreference = 'Stop'
            . $HelperPath
            Get-PureRdpPackageVersion $StatePath -Now '2026-10-20T12:00:00+08:00'
        } -ArgumentList $helper, $concurrent
    })
    $jobs | Wait-Job -Timeout 30 | Out-Null
    Check (@($jobs | Where-Object State -ne 'Completed').Count -eq 0) 'Concurrent version allocation completes.'
    $versions = @($jobs | Receive-Job -ErrorAction Stop)
    Check ($versions.Count -eq 4 -and @($versions | Where-Object { $_ -ne '1.0.1' }).Count -eq 0) 'Concurrent processes allocate just one daily increment.'
    Check ((Get-PureRdpCurrentVersion $concurrent) -eq '1.0.1') 'Concurrent state persists a single increment.'
} finally {
    $jobs | Stop-Job -ErrorAction SilentlyContinue
    $jobs | Remove-Job -Force -ErrorAction SilentlyContinue
}
Check ((Get-FileHash -LiteralPath $production).Hash -eq $productionHash) 'Tests must never change production version/date.'
Write-Host "PASS: $script:checks daily-version checks; synthetic props only, production version unchanged."
