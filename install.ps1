[CmdletBinding()]
param(
    [switch]$NoStartup,
    [switch]$NoLaunch,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installer = Join-Path $projectRoot "windows-native\install-user.ps1"
$starter = Join-Path $projectRoot "start.ps1"

Write-Host ""
Write-Host "Speedysearch Windows install" -ForegroundColor Cyan
Write-Host "Building and installing for the current Windows user..."

$installArguments = @{}
if ($SkipBuild) {
    $installArguments["SkipBuild"] = $true
}
if ($NoStartup) {
    $installArguments["NoStartAtLogin"] = $true
} else {
    $installArguments["StartAtLogin"] = $true
}

& $installer @installArguments

if (-not $NoLaunch) {
    & $starter
}

Write-Host ""
Write-Host "Installation complete." -ForegroundColor Green
if (-not $NoStartup) {
    Write-Host "Speedysearch will start quietly whenever you sign in to Windows."
}
Write-Host "Press Ctrl+Space to show or hide the launcher."
