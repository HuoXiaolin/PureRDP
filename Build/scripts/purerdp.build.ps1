# Ensure unsigned powershell script execution ist allowed: Set-ExecutionPolicy -ExecutionPolicy RemoteSigned

param (
  [Parameter()][String]$version
)

. $PSScriptRoot/_core.ps1

Push-Location $PSScriptRoot/../..

# Configure
if (!$version) {
	$version = '2.0.0'
}
$version = $version -replace '^[vV]', ''
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Version must be numeric, e.g. 2.0.0 or v2.0.0.' }

# Build
Build-Project -project PureRDP -version $version

Pop-Location
