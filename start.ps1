[CmdletBinding()]
param(
    [switch]$Hidden
)

$ErrorActionPreference = "Stop"
$executable = Join-Path $env:LOCALAPPDATA "Programs\Speedysearch\Speedysearch.exe"

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Speedysearch is not installed. Run .\install.cmd first."
}

if ($Hidden) {
    Start-Process -FilePath $executable -ArgumentList "--hidden"
} else {
    Start-Process -FilePath $executable
}

if (-not $Hidden) {
    Write-Host "Speedysearch started. Press Ctrl+Space to show or hide it."
}
