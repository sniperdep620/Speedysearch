[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$outputDirectory = Join-Path (Join-Path $projectRoot "bin") $Configuration

if (-not $SkipBuild) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $projectRoot "build.ps1") -Configuration $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "Production build failed."
    }
}

$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot "src") -Filter "*.cs" |
    Sort-Object Name |
    ForEach-Object { $_.FullName }
$tests = Get-ChildItem -LiteralPath (Join-Path $projectRoot "tests") -Filter "*.cs" |
    Sort-Object Name |
    ForEach-Object { $_.FullName }
$references = @(
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Security.dll",
    "/reference:System.Web.Extensions.dll",
    "/reference:System.Windows.Forms.dll"
)
$options = @(
    "/nologo",
    "/langversion:5",
    "/platform:anycpu",
    "/target:exe",
    "/warn:4",
    "/main:Speedysearch.Windows.Tests.TestRunner",
    "/out:$(Join-Path $outputDirectory 'speedysearch-tests.exe')"
) + $references
if ($Configuration -eq "Release") {
    $options += @("/optimize+", "/debug:pdbonly")
} else {
    $options += @("/optimize-", "/debug:full", "/define:DEBUG")
}

Write-Host "Compiling Windows test suite..."
& $compiler @options $sources $tests
if ($LASTEXITCODE -ne 0) {
    throw "Test compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Running Windows test suite..."
& (Join-Path $outputDirectory "speedysearch-tests.exe")
if ($LASTEXITCODE -ne 0) {
    throw "Windows test suite failed with exit code $LASTEXITCODE"
}

$installerPath = Join-Path $projectRoot "install-user.ps1"
$installer = Get-Content -LiteralPath $installerPath -Raw
if ($installer.IndexOf('speedysearch-ui.exe', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) {
    throw "Windows installer does not select the Tauri launcher."
}
if ($installer.IndexOf('Join-Path $buildDirectory "Speedysearch.exe"', [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
    throw "Windows installer still selects the legacy WinForms launcher."
}
Write-Host "PASS  Windows installer selects the shared Tauri/React GUI"

$repositoryRoot = Split-Path -Parent $projectRoot
$tauriMainPath = Join-Path $repositoryRoot "src-tauri\src\main.rs"
$tauriMain = Get-Content -LiteralPath $tauriMainPath -Raw
if ($tauriMain.IndexOf('#![cfg_attr(target_os = "windows", windows_subsystem = "windows")]', [System.StringComparison]::Ordinal) -lt 0) {
    throw "The Windows Tauri launcher is not configured as a GUI application; launching it would open a console window."
}
Write-Host "PASS  Windows Tauri launcher uses the GUI subsystem"

$baseTauriConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot "src-tauri\tauri.conf.json") -Raw |
    ConvertFrom-Json
$windowsTauriConfig = Get-Content -LiteralPath (Join-Path $repositoryRoot "src-tauri\tauri.windows.conf.json") -Raw |
    ConvertFrom-Json
if ($baseTauriConfig.build.frontendDist -ne "../frontend/dist") {
    throw "Tauri does not use the shared frontend distribution."
}
if ($null -ne $windowsTauriConfig.PSObject.Properties["app"]) {
    throw "Windows Tauri configuration overrides the shared application UI."
}
Write-Host "PASS  Windows and Linux use the identical React frontend and window definition"
