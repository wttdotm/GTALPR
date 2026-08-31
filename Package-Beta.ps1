[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$projectFile = Join-Path $repoRoot "FlockSurveillance.csproj"
$releaseOutput = Join-Path $repoRoot "bin\Release\net48"
$distRoot = Join-Path $repoRoot "dist"
$packageRoot = Join-Path $distRoot "GTALPR-beta"
$dlcSource = Join-Path $repoRoot "packaging\gtalpr\dlc.rpf"

if (-not (Test-Path -LiteralPath $dlcSource -PathType Leaf))
{
    throw "Missing standalone GTALPR DLC archive: $dlcSource"
}

& dotnet build $projectFile --configuration Release

if ($LASTEXITCODE -ne 0)
{
    throw "GTALPR Release build failed with exit code $LASTEXITCODE."
}

$requiredBuildFiles = @(
    (Join-Path $releaseOutput "GTALPR.dll"),
    (Join-Path $releaseOutput "LemonUI.SHVDN3.dll")
)

foreach ($requiredBuildFile in $requiredBuildFiles)
{
    if (-not (Test-Path -LiteralPath $requiredBuildFile -PathType Leaf))
    {
        throw "Release build did not produce: $requiredBuildFile"
    }
}

# Keep recursive replacement constrained to one exact, repository-owned path.
$expectedPackageRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repoRoot "dist\GTALPR-beta")
)
$resolvedPackageRoot = [System.IO.Path]::GetFullPath($packageRoot)

if (-not [string]::Equals(
    $resolvedPackageRoot,
    $expectedPackageRoot,
    [System.StringComparison]::OrdinalIgnoreCase
))
{
    throw "Refusing unexpected package output path: $resolvedPackageRoot"
}

if (Test-Path -LiteralPath $resolvedPackageRoot)
{
    Remove-Item -LiteralPath $resolvedPackageRoot -Recurse -Force
}

$scriptsDirectory = Join-Path $resolvedPackageRoot "scripts"
$dlcDirectory = Join-Path $resolvedPackageRoot (
    "mods\update\x64\dlcpacks\gtalpr"
)

New-Item -ItemType Directory -Path $scriptsDirectory -Force |
    Out-Null
New-Item -ItemType Directory -Path $dlcDirectory -Force |
    Out-Null

$instructionsSource = Join-Path $repoRoot "INSTRUCTIONS.md"
$instructionsDestination = Join-Path $resolvedPackageRoot "INSTRUCTIONS.md"
Copy-Item -LiteralPath $instructionsSource -Destination $instructionsDestination -Force

$modDllSource = Join-Path $releaseOutput "GTALPR.dll"
$modDllDestination = Join-Path $scriptsDirectory "GTALPR.dll"
Copy-Item -LiteralPath $modDllSource -Destination $modDllDestination -Force

$lemonUiSource = Join-Path $releaseOutput "LemonUI.SHVDN3.dll"
$lemonUiDestination = Join-Path $scriptsDirectory "LemonUI.SHVDN3.dll"
Copy-Item -LiteralPath $lemonUiSource -Destination $lemonUiDestination -Force

$catalogSource = Join-Path $repoRoot "in_game_cameras.json"
$catalogDestination = Join-Path $scriptsDirectory "in_game_cameras.json"
Copy-Item -LiteralPath $catalogSource -Destination $catalogDestination -Force

$dlcDestination = Join-Path $dlcDirectory "dlc.rpf"
Copy-Item -LiteralPath $dlcSource -Destination $dlcDestination -Force

Write-Host ""
Write-Host "GTALPR beta package created at:"
Write-Host $resolvedPackageRoot
