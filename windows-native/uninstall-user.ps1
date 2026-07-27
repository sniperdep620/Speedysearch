[CmdletBinding()]
param(
    [switch]$PurgeLocalData
)

$ErrorActionPreference = "Stop"
$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\Speedysearch"
$shortcutPath = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Speedysearch.lnk"
$dataDirectory = Join-Path $env:LOCALAPPDATA "Speedysearch"
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if (Test-Path -LiteralPath $shortcutPath) {
    Remove-Item -LiteralPath $shortcutPath -Force
}

$runValue = (Get-ItemProperty -Path $runPath -Name "Speedysearch" -ErrorAction SilentlyContinue).Speedysearch
if ($runValue -and $runValue.IndexOf($installDirectory, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    Remove-ItemProperty -Path $runPath -Name "Speedysearch"
}

$resolvedInstallParent = [System.IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "Programs"))
$resolvedInstall = [System.IO.Path]::GetFullPath($installDirectory)
if ($resolvedInstall.StartsWith($resolvedInstallParent + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -and
    (Test-Path -LiteralPath $resolvedInstall)) {
    Remove-Item -LiteralPath $resolvedInstall -Recurse -Force
}

if ($PurgeLocalData) {
    $resolvedLocal = [System.IO.Path]::GetFullPath($env:LOCALAPPDATA)
    $resolvedData = [System.IO.Path]::GetFullPath($dataDirectory)
    if ($resolvedData.StartsWith($resolvedLocal + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedData)) {
        Remove-Item -LiteralPath $resolvedData -Recurse -Force
        Write-Host "Removed local index, configuration, clickstream, and model data."
    }
}

Write-Host "Speedysearch was removed for the current user."
