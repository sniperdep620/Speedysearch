[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputRoot = Join-Path $projectRoot "bin"
$outputDirectory = Join-Path $outputRoot $Configuration
$compiler = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (-not (Test-Path -LiteralPath $compiler)) {
    throw ".NET Framework 4.x C# compiler was not found at $compiler"
}

if ($Clean -and (Test-Path -LiteralPath $outputRoot)) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$sources = Get-ChildItem -LiteralPath (Join-Path $projectRoot "src") -Filter "*.cs" |
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
$common = @(
    "/nologo",
    "/langversion:5",
    "/platform:anycpu",
    "/warn:4",
    "/main:Speedysearch.Windows.Program"
) + $references

if ($Configuration -eq "Release") {
    $common += @("/optimize+", "/debug:pdbonly")
} else {
    $common += @("/optimize-", "/debug:full", "/define:DEBUG")
}

Write-Host "Building legacy native GUI for backend comparison..."
& $compiler @common "/target:winexe" "/out:$(Join-Path $outputDirectory 'Speedysearch.Native.exe')" $sources
if ($LASTEXITCODE -ne 0) {
    throw "Native reference GUI compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Building Windows command-line and daemon host..."
& $compiler @common "/target:exe" "/out:$(Join-Path $outputDirectory 'speedysearch-cli.exe')" $sources
if ($LASTEXITCODE -ne 0) {
    throw "CLI compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Building compatibility launcher for the Tauri/React GUI..."
$bootstrapSource = Join-Path $projectRoot "src\TauriBootstrap.cs"
$bootstrapOptions = $common | Where-Object { -not $_.StartsWith("/main:", [System.StringComparison]::OrdinalIgnoreCase) }
& $compiler @bootstrapOptions "/main:Speedysearch.Windows.TauriBootstrap" "/target:winexe" "/out:$(Join-Path $outputDirectory 'Speedysearch.exe')" $bootstrapSource
if ($LASTEXITCODE -ne 0) {
    throw "Tauri compatibility launcher compilation failed with exit code $LASTEXITCODE"
}

Write-Host "Build complete: $outputDirectory"
