<#
.SYNOPSIS
    Publishes Omega Asset Studio 2 as a downloadable build.

.DESCRIPTION
    Produces a self-contained x64 folder and zips it. Self-contained on purpose:
    the .NET runtime and the Windows App SDK both travel with the app, so
    somebody who downloads it can unzip and run without installing anything
    first. The cost is size, and that is the right trade for a tool whose users
    are not developers.

    The zip is named for the version in Directory.Build.props, which is the one
    place a version is set.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER OutputRoot
    Where to put the published folder and the zip. Defaults to dist\ beside the
    repository, so build output never lands inside the tree.

.PARAMETER SkipTests
    Publish without running the test suite first. Not the default: a build
    nobody can download is better than one that is wrong.

.EXAMPLE
    .\build\publish.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path $PSScriptRoot -Parent
$appProject = Join-Path $repo 'src\OmegaAssetStudio2.App\OmegaAssetStudio2.App.csproj'
$solution = Join-Path $repo 'OmegaAssetStudio2.sln'

if (-not $OutputRoot) { $OutputRoot = Join-Path (Split-Path $repo -Parent) 'OmegaAssetStudio2_dist' }

# The version the assemblies are stamped with, read rather than repeated.
[xml]$props = Get-Content (Join-Path $repo 'Directory.Build.props')
$version = ($props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1).ToString().Trim()
if (-not $version) { throw 'No <Version> found in Directory.Build.props.' }

Write-Host "Publishing Omega Asset Studio 2 $version ($Configuration, x64, self-contained)..." -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host 'Running tests...' -ForegroundColor Cyan
    & dotnet test -c $Configuration -p:Platform=x64 $solution
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE. Nothing published." }
}

$stage = Join-Path $OutputRoot "OmegaAssetStudio2-$version-win-x64"
if (Test-Path $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

& dotnet publish $appProject `
    -c $Configuration `
    -p:Platform=x64 `
    -r win-x64 `
    --self-contained true `
    -p:WindowsAppSDKSelfContained=true `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "Publish failed with exit code $LASTEXITCODE." }

$exe = Join-Path $stage 'OmegaAssetStudio2.exe'
if (-not (Test-Path $exe)) { throw "Publish reported success but the executable is missing at:`n  $exe" }

# The licence notices travel with the binaries, because that is what they are
# obligations about.
foreach ($doc in 'LICENSE', 'THIRD_PARTY_NOTICES.txt', 'README.md') {
    $path = Join-Path $repo $doc
    if (Test-Path $path) { Copy-Item $path (Join-Path $stage $doc) -Force }
}

# The debug symbols are for us, not for somebody downloading a tool.
Get-ChildItem $stage -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

$zip = Join-Path $OutputRoot "OmegaAssetStudio2-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

$sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
$fileCount = (Get-ChildItem $stage -Recurse -File).Count

Write-Host ''
Write-Host "Published $fileCount files" -ForegroundColor Green
Write-Host "  folder : $stage" -ForegroundColor Green
Write-Host "  zip    : $zip  ($sizeMb MB)" -ForegroundColor Green
