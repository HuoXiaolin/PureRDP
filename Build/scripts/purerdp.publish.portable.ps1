# Ensure unsigned powershell script execution ist allowed: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned

param (
  [Parameter(Mandatory=$false)][String]$version,
  [Parameter(Mandatory=$false)][String]$runtime,
  [Parameter(Mandatory=$false)][String]$publishFolder
)

. $PSScriptRoot/_core.ps1

Push-Location $PSScriptRoot/../..

# Configure
$framework = "net10.0-windows"
$version = Get-PureRdpPackageVersion -VersionFile (Join-Path $PSScriptRoot '../targets/version.props') -ExplicitVersion $version

# Publish
Publish-Project -project PureRDP -version $version -framework $framework -runtime $runtime -publishFolder $publishFolder

Pop-Location
