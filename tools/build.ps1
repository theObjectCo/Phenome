<#
.SYNOPSIS
    Builds all three parts of the link into dist/: the Grasshopper plugin, the Rhino plugin and the VS
    Code extension.

.DESCRIPTION
    The three are one mechanism and share one version. The .gha is the canvas end. The .vsix is the editor
    end, and the canvas's pair button passes it to VS Code on first pairing. A release that carries only one
    of them is incomplete.

    The .rhp is the Rhino end and the easiest to forget. A .gha is loaded only once Grasshopper has started,
    and nothing else can report on a dialog that appears while Rhino is still starting, which is when nothing
    else can answer.

    The .mcpb is the Claude Desktop extension, packed last from the other files by tools/pack-mcpb.ps1.

    Nothing here talks to a package server. Publishing is a separate, deliberate act.

.PARAMETER Configuration
    Release by default, which is the configuration that ships, with no symbols and no machine paths.

.EXAMPLE
    pwsh tools/build.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $repo 'dist'
$plugin = Join-Path $repo 'src/Phenome.Apps.GrasshopperLink'
$rhinoPlugin = Join-Path $repo 'src/Phenome.Apps.RhinoLink'
$extension = Join-Path $repo 'src/Phenome.Apps.VSCodeLink'

if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Building the Grasshopper plugin ($Configuration)..." -ForegroundColor Cyan
dotnet build (Join-Path $plugin 'Phenome.Apps.GrasshopperLink.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'The plugin did not build.' }

$gha = Get-ChildItem -Recurse (Join-Path $plugin "bin/$Configuration") -Filter '*.gha' |
    Select-Object -First 1
if (-not $gha) { throw 'The build produced no .gha.' }
Copy-Item $gha.FullName $dist
Copy-Item (Join-Path $plugin 'manifest.yml') $dist

Write-Host "Building the Rhino plugin ($Configuration)..." -ForegroundColor Cyan
dotnet build (Join-Path $rhinoPlugin 'Phenome.Apps.RhinoLink.csproj') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'The Rhino plugin did not build.' }

$rhp = Get-ChildItem -Recurse (Join-Path $rhinoPlugin "bin/$Configuration") -Filter '*.rhp' |
    Select-Object -First 1
if (-not $rhp) { throw 'The build produced no .rhp.' }
Copy-Item $rhp.FullName $dist

Write-Host 'Packaging the VS Code extension...' -ForegroundColor Cyan
Push-Location $extension
try {
    npx --yes @vscode/vsce package --allow-missing-repository --skip-license
    if ($LASTEXITCODE -ne 0) { throw 'The extension did not package.' }
}
finally {
    Pop-Location
}

$vsix = Get-ChildItem $extension -Filter 'phenome-link-*.vsix' |
    Sort-Object Name -Descending |
    Select-Object -First 1
if (-not $vsix) { throw 'Packaging produced no .vsix.' }
Move-Item $vsix.FullName $dist

Write-Host 'Packing the Claude Desktop extension...' -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'pack-mcpb.ps1') -Dist $dist

Write-Host ''
Write-Host 'dist/' -ForegroundColor Green
Get-ChildItem $dist | ForEach-Object {
    Write-Host ("  {0,-44} {1,7:N0} KB" -f $_.Name, ($_.Length / 1KB))
}
