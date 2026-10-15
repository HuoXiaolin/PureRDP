# Keep reusable build dependencies with the local toolchain, outside source trees.
if ($env:GITHUB_ACTIONS -eq 'true') {
    $env:NUGET_PACKAGES = Join-Path $env:RUNNER_TEMP 'nuget\packages'
} elseif (Test-Path -LiteralPath 'D:\Agant\toolchain') {
    $env:NUGET_PACKAGES = 'D:\Agant\toolchain\nuget\packages'
} elseif ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    $env:NUGET_PACKAGES = Join-Path $env:USERPROFILE '.nuget\packages'
}
