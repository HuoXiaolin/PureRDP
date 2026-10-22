# Ensure unsigned powershell script execution ist allowed: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned

param (
  [Parameter()][String]$version,
  [Parameter()][String]$Dotnet = ''
)

. $PSScriptRoot/_core.ps1
if (-not [string]::IsNullOrWhiteSpace($Dotnet)) { $script:DotnetCommand = $Dotnet }

Push-Location $PSScriptRoot/../..

# Configure
if (!$version) {
	$version = Get-PureRdpCurrentVersion (Join-Path $PSScriptRoot '../targets/version.props')
}
$version = $version -replace '^[vV]', ''
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must be numeric, e.g. 2.0.0 or v2.0.0.' }

# Build
Build-Project -project PureRDP -version $version

Pop-Location
