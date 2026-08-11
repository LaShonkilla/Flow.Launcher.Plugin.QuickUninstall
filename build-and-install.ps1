$ErrorActionPreference = 'Stop'

Write-Host "Building Quick Uninstall..." -ForegroundColor Cyan

dotnet restore
# Official Flow template currently targets net7.0-windows.
dotnet build -c Release

$source = Join-Path $PSScriptRoot 'bin\Release'
$pluginRoot = Join-Path $env:APPDATA 'FlowLauncher\Plugins'
$target = Join-Path $pluginRoot 'QuickUninstall-1.0.9'

if (!(Test-Path $source)) {
    throw "Build output not found: $source"
}

# Remove older QuickUninstall folders when possible so Flow doesn't see duplicate versions.
Get-ChildItem -Path $pluginRoot -Directory -Filter 'QuickUninstall-*' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -ne $target } |
    ForEach-Object {
        try {
            Remove-Item -Path $_.FullName -Recurse -Force -ErrorAction Stop
            Write-Host "Removed old plugin: $($_.Name)" -ForegroundColor DarkGray
        }
        catch {
            Write-Host "Could not remove old plugin $($_.Name). Close Flow Launcher and delete it manually if both versions appear." -ForegroundColor Yellow
        }
    }

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force

Write-Host ""
Write-Host "Installed to:" -ForegroundColor Green
Write-Host $target
Write-Host ""
Write-Host "Restart Flow Launcher, then type: un" -ForegroundColor Yellow
Write-Host "Examples: un | un - | un date | un -date | un size | un -size | un stat" -ForegroundColor Yellow
