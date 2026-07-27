[CmdletBinding()]
param(
    [string]$Python = "python",
    [int]$MinimumSamples = 50
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$trainingScript = Join-Path (Split-Path -Parent $projectRoot) "train\train_ranker.py"
$clickstream = Join-Path $env:LOCALAPPDATA "Speedysearch\Data\clickstream.jsonl"
$model = Join-Path $env:LOCALAPPDATA "Speedysearch\Data\ranker.txt"

if (-not (Test-Path -LiteralPath $trainingScript)) {
    throw "Training script was not found at $trainingScript"
}
if (-not (Test-Path -LiteralPath $clickstream)) {
    throw "No Windows clickstream exists yet at $clickstream"
}

& $Python $trainingScript --clickstream $clickstream --output $model --min-samples $MinimumSamples
if ($LASTEXITCODE -ne 0) {
    throw "Ranker training failed with exit code $LASTEXITCODE"
}
