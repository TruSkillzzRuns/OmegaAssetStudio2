<#
.SYNOPSIS
    Builds Omega Asset Studio 2.

.DESCRIPTION
    Wraps the one correct build invocation so nobody has to remember the flags.
    -p:Platform=x64 is mandatory: without it the output lands in a directory the
    app is never launched from, and the next test silently reruns the old binary.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER Test
    Also run the test suite.

.PARAMETER Clean
    Delete build output before building.

.EXAMPLE
    .\build\build.ps1
    .\build\build.ps1 -Configuration Debug -Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Test,
    [switch]$Clean
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$solution = Join-Path $repo 'OmegaAssetStudio2.sln'
$appProject = Join-Path $repo 'src\OmegaAssetStudio2.App\OmegaAssetStudio2.App.csproj'

if ($Clean) {
    Write-Host 'Cleaning build output...' -ForegroundColor Cyan
    Get-ChildItem $repo -Recurse -Directory -Include bin, obj |
        Where-Object { $_.FullName -notmatch '\\node_modules\\' } |
        ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "Building $Configuration x64..." -ForegroundColor Cyan
& dotnet build -c $Configuration -p:Platform=x64 $solution
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

if ($Test) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    & dotnet test -c $Configuration -p:Platform=x64 --no-build $solution
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $repo "src\OmegaAssetStudio2.App\bin\x64\$Configuration\net8.0-windows10.0.19041.0\win-x64\OmegaAssetStudio2.exe"
if (Test-Path $exe) {
    Write-Host "`nBuilt: $exe" -ForegroundColor Green
} else {
    Write-Warning "Build reported success but the executable is missing at:`n  $exe"
}
