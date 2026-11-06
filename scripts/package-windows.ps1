# Windows installer: dotnet publish win-x64, then Inno Setup → dist/steamrip-desktop-<version>-setup.exe
#   scripts/package-windows.ps1 [-Version 1.2.3]
# Needs Inno Setup 6 (ISCC.exe on PATH or in its default install dir); windows-latest runners have it.
[CmdletBinding()]
param([string]$Version)
$ErrorActionPreference = "Stop"
Set-Location (git rev-parse --show-toplevel)

if (-not $Version) {
    $tag = git describe --tags --match 'v*' --abbrev=0 2>$null
    $Version = if ($tag) { $tag.TrimStart('v') } else { "0.0.0-dev" }
}

Write-Host "== publish win-x64 ($Version)"
dotnet publish src/SteamRip.App -c Release -r win-x64 -o out/win-x64 --nologo -v quiet `
    -p:Version=$Version -p:DebugType=none -p:TreatWarningsAsErrors=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
$isccPath = if ($iscc) { $iscc.Source } else {
    @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $isccPath) { throw "Inno Setup 6 not found (ISCC.exe). Install it or add it to PATH." }

Write-Host "== Inno Setup"
New-Item -ItemType Directory -Force dist | Out-Null
& $isccPath /Q "/DAppVersion=$Version" packaging\windows\setup.iss
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Get-ChildItem dist\*-setup.exe | ForEach-Object { Write-Host "   dist\$($_.Name)" }
