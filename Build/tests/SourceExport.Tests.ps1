$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../scripts/_source-export.ps1"
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('purerdp-export-' + [Guid]::NewGuid().ToString('N'))
$stage = Join-Path $fixture 'stage'
$target = Join-Path $fixture 'target'
[void](New-Item -ItemType Directory -Path $stage, $target -Force)
function Check($condition, $message) { if (-not $condition) { throw $message } }
& git -C $target init --quiet
if ($LASTEXITCODE) { throw 'Fixture git init failed.' }
& git -C $target config remote.origin.url 'https://example.invalid/demo.git'
$gitConfig = Get-FileHash -LiteralPath (Join-Path $target '.git/config')
$gitHead = Get-FileHash -LiteralPath (Join-Path $target '.git/HEAD')
[IO.File]::WriteAllText((Join-Path $target 'owned.txt'), 'old')
[IO.File]::WriteAllText((Join-Path $target 'user-note.txt'), 'keep')
[IO.File]::WriteAllText((Join-Path $stage 'owned.txt'), 'new')
[IO.File]::WriteAllText((Join-Path $stage '.gitignore'), '*.tmp')
Publish-PureRdpSourceFiles $stage $target (Join-Path $fixture 'backup1')
Check ([IO.File]::ReadAllText((Join-Path $target 'owned.txt')) -eq 'new') 'Source not refreshed.'
Check ([IO.File]::ReadAllText((Join-Path $fixture 'backup1/owned.txt')) -eq 'old') 'Previous source not backed up.'
Check ([IO.File]::ReadAllText((Join-Path $target 'user-note.txt')) -eq 'keep') 'Unknown file lost.'
Publish-PureRdpSourceFiles $stage $target (Join-Path $fixture 'backup2')
Check ((Get-FileHash -LiteralPath (Join-Path $target '.git/config')).Hash -eq $gitConfig.Hash) 'Git config changed.'
Check ((Get-FileHash -LiteralPath (Join-Path $target '.git/HEAD')).Hash -eq $gitHead.Hash) 'Git HEAD changed.'
[IO.File]::WriteAllText((Join-Path $stage '.git'), 'bad')
$rejected = $false
try { Publish-PureRdpSourceFiles $stage $target (Join-Path $fixture 'backup3') } catch { $rejected = $true }
Check $rejected 'Staged .git must be rejected.'
Check ((Get-FileHash -LiteralPath (Join-Path $target '.git/HEAD')).Hash -eq $gitHead.Hash) 'Rejected export altered Git.'
$rejected = $false
try { Publish-PureRdpSourceFiles $stage $target (Join-Path $target 'nested-backup') } catch { $rejected = $true }
Check $rejected 'Nested backup must be rejected.'
Write-Host "PASS: repeated source export, Git identity, backup, unrelated file and path protection; fixtures: $fixture"
