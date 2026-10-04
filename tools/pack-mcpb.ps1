<#
.SYNOPSIS
    Packs the Claude Desktop extension from what a build left in dist/.

.DESCRIPTION
    The extension is the MCP server from the VS Code extension with both plug-ins beside it, in server/rhino/.
    The server looks there when it starts and installs the plug-ins into Rhino's package folder. Packing from
    dist/ puts exactly the plug-ins of that build into the extension.

    tools/build.ps1 and the workflow both call this script, and this is the one place that says what the
    extension contains. Each file is named, for the reason tools/pack-yak.ps1 gives: a wildcard that matches
    nothing packs an extension that installs nothing, with no error.

.EXAMPLE
    pwsh tools/pack-mcpb.ps1
#>
[CmdletBinding()]
param(
    [string] $Dist = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist')
)

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$desktop = Join-Path $repo 'src/Phenome.Apps.ClaudeDesktopLink'
$version = (Get-Content (Join-Path $desktop 'manifest.json') -Raw | ConvertFrom-Json).version
$stage = Join-Path ([IO.Path]::GetTempPath()) 'phenome-mcpb'

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory -Force (Join-Path $stage 'server/rhino') | Out-Null

Copy-Item (Join-Path $desktop 'manifest.json'), (Join-Path $desktop 'icon.png') $stage
Copy-Item (Join-Path $repo 'src/Phenome.Apps.VSCodeLink/mcp.js') (Join-Path $stage 'server')

foreach ($name in 'Phenome.Apps.GrasshopperLink.gha', 'Phenome.Apps.RhinoLink.rhp', 'manifest.yml') {
    $file = Join-Path $Dist $name
    if (-not (Test-Path $file)) { throw "The extension needs $name and $Dist has none. Build first." }
    Copy-Item $file (Join-Path $stage 'server/rhino')
}

# Pinned for the reason vsce is pinned in the workflow: an unpinned packer can change what a release contains
# without a commit here.
npx --yes @anthropic-ai/mcpb@2.1.2 pack $stage (Join-Path $Dist "phenome-link-$version.mcpb")
if ($LASTEXITCODE -ne 0) { throw 'The Claude Desktop extension did not pack.' }
