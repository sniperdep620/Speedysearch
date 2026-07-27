[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$StartAtLogin,
    [switch]$NoStartAtLogin
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = Split-Path -Parent $projectRoot
$buildDirectory = Join-Path $projectRoot "bin\Release"
$tauriBuildDirectory = Join-Path $repositoryRoot "src-tauri\target\release"
$installDirectory = Join-Path $env:LOCALAPPDATA "Programs\Speedysearch"
$startMenuDirectory = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$shortcutPath = Join-Path $startMenuDirectory "Speedysearch.lnk"
$configDirectory = Join-Path $env:LOCALAPPDATA "Speedysearch"
$configPath = Join-Path $configDirectory "config.json"
$exampleConfigPath = Join-Path $projectRoot "config.example.json"
$runPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if ($StartAtLogin -and $NoStartAtLogin) {
    throw "Use either -StartAtLogin or -NoStartAtLogin, not both."
}

function Set-StartupPreference {
    param([bool]$Enabled)

    try {
        if (Test-Path -LiteralPath $configPath) {
            $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        } else {
            $config = Get-Content -LiteralPath $exampleConfigPath -Raw | ConvertFrom-Json
            New-Item -ItemType Directory -Path $configDirectory -Force | Out-Null
        }

        $startupProperty = $config.PSObject.Properties["RunAtStartup"]
        if ($null -eq $startupProperty) {
            $config | Add-Member -MemberType NoteProperty -Name "RunAtStartup" -Value $Enabled
        } else {
            $startupProperty.Value = $Enabled
        }

        $json = $config | ConvertTo-Json -Depth 10
        $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($configPath, $json, $utf8WithoutBom)
    } catch {
        Write-Warning "Could not update the Run at sign-in preference in $configPath`: $($_.Exception.Message)"
    }
}

if (-not $SkipBuild) {
    $npm = Get-Command npm.cmd -ErrorAction SilentlyContinue
    if ($null -eq $npm) {
        throw "npm.cmd was not found. Install Node.js, then run npm install before installing Speedysearch."
    }
    Push-Location $repositoryRoot
    try {
        & $npm.Source run tauri build -- --no-bundle
        if ($LASTEXITCODE -ne 0) {
            throw "Tauri release build failed with exit code $LASTEXITCODE."
        }
    } finally {
        Pop-Location
    }
}

$guiSource = Join-Path $tauriBuildDirectory "speedysearch-ui.exe"
$cliSource = Join-Path $buildDirectory "speedysearch-cli.exe"
if (-not (Test-Path -LiteralPath $guiSource) -or -not (Test-Path -LiteralPath $cliSource)) {
    throw "Release binaries are missing. Run npm run tauri build -- --no-bundle first."
}

New-Item -ItemType Directory -Path $installDirectory -Force | Out-Null
# Keep the friendly installed filename for existing shortcuts and upgrades, but
# its source is now the Tauri binary. The old WinForms launcher is never copied.
Copy-Item -LiteralPath $guiSource -Destination (Join-Path $installDirectory "Speedysearch.exe") -Force
Copy-Item -LiteralPath $cliSource -Destination (Join-Path $installDirectory "speedysearch-cli.exe") -Force

New-Item -ItemType Directory -Path $startMenuDirectory -Force | Out-Null
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $installDirectory "Speedysearch.exe"
$shortcut.WorkingDirectory = $installDirectory
$shortcut.Description = "Fast, private Windows desktop search"
$shortcut.IconLocation = "$(Join-Path $installDirectory 'Speedysearch.exe'),0"
$shortcut.Save()

if ($StartAtLogin) {
    New-Item -Path $runPath -Force | Out-Null
    Set-ItemProperty -Path $runPath -Name "Speedysearch" -Value "`"$(Join-Path $installDirectory 'Speedysearch.exe')`" --hidden"
    Set-StartupPreference -Enabled $true
} elseif ($NoStartAtLogin) {
    Remove-ItemProperty -Path $runPath -Name "Speedysearch" -ErrorAction SilentlyContinue
    Set-StartupPreference -Enabled $false
}

Write-Host "Speedysearch installed for the current user."
Write-Host "Application: $installDirectory"
Write-Host "Start Menu:  $shortcutPath"
if ($StartAtLogin) {
    Write-Host "Startup:     enabled (Speedysearch will run after you sign in)"
} elseif ($NoStartAtLogin) {
    Write-Host "Startup:     disabled"
} else {
    Write-Host "Startup:     unchanged"
}
