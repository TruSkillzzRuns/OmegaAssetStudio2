<#
.SYNOPSIS
    Builds and launches Omega Asset Studio 2.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration
}

$exe = Join-Path $repo "src\OmegaAssetStudio2.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\OmegaAssetStudio2.exe"
if (-not (Test-Path $exe)) { throw "Executable not found at $exe. Build first." }

Write-Host "Launching $exe" -ForegroundColor Cyan
Start-Process -FilePath $exe
