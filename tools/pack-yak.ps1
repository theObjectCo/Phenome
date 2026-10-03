# Packs Grasshopper plugin packages for a private Yak source.
#
# A private source is a folder of .yak files added as a package source in Rhino. Yak has no separate permission
# model, so file-system access controls installation.
#
#   pwsh tools/pack-yak.ps1                        # remembered destination, default dist/yak
#   pwsh tools/pack-yak.ps1 -Destination <folder>  # pack to a specific folder
#   pwsh tools/pack-yak.ps1 -From dist             # pack existing build output without rebuilding
#
# The remembered destination is stored in tools/yak-destination.txt (gitignored) or PHENOME_YAK_DESTINATION.
# Machine-specific share paths are not stored in the repository.
#
# The link package includes the VS Code extension beside the .gha. The pair button passes the vsix to VS Code
# before the first pairing, and one package installs both parts.
#
# -From keeps the definition of package contents in this one script, and every caller shares its content rules
# and checks (see Requires below). No CI job runs this script; it is run by hand.

[CmdletBinding()]
param(
    [string] $Destination,
    [string] $Yak = 'C:\Program Files\Rhino 8\System\Yak.exe',

    # A folder containing prior build output. When provided, those files are used as package inputs and nothing
    # is rebuilt. When omitted, the projects are built and their outputs are collected.
    [string] $From,

    # Expected package version, typically the CI tag. The Yak output filename is checked against it to confirm
    # that the expected manifest was used.
    [string] $ExpectVersion
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (-not $Destination) {
    $remembered = Join-Path $PSScriptRoot 'yak-destination.txt'

    $Destination =
        if ($env:PHENOME_YAK_DESTINATION) { $env:PHENOME_YAK_DESTINATION }
        elseif (Test-Path $remembered) { (Get-Content $remembered -Raw).Trim() }
        else { Join-Path $PSScriptRoot '..\dist\yak' }
}

if (-not (Test-Path $Yak)) {
    throw "Yak is not at $Yak - point -Yak at it (it ships inside Rhino's System folder)."
}

New-Item -ItemType Directory -Force $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

# Only the link package is distributed now. The components plugin contains the kernel and has a manifest, but is
# not distributed yet; add it to this list when packaging starts.
$packages = @(
    @{
        Name    = 'phenome-link'
        Project = 'src/Phenome.Apps.GrasshopperLink'
        # Required package contents, by each file's staged name. This is the one definition checked after
        # staging, for -From and for local builds alike. {version} is filled from the manifest.
        #
        # These are requirements and not search paths. tools/build.ps1 moves the VS Code extension to dist/, and
        # a wildcard search in the packaging folder could then produce a package without a .vsix and without an
        # error, while the README still says the pair button installs one. Wildcard matching also selects by
        # string order, where 0.9.0 sorts above 0.22.0, and an old leftover file could replace the current build.
        # Naming the required files and failing without them prevents both cases.
        #
        # The Rhino plugin is included because the components ship together, and the plugin that reports a stuck
        # Rhino is no use when it is not installed.
        Requires = @(
            'Phenome.Apps.GrasshopperLink.gha',
            'Phenome.Apps.RhinoLink.rhp',
            'phenome-link-{version}.vsix',
            'manifest.yml')

        # Candidate source locations when building from source. Several entries may match the same file: the
        # extension can be in dist/ after a full build or beside its project after a bare vsce run. Requires
        # decides whether the staged package is complete.
        Sources  = @(
            'src/Phenome.Apps.RhinoLink/bin/Release/net7.0/Phenome.Apps.RhinoLink.rhp',
            'dist/phenome-link-{version}.vsix',
            'src/Phenome.Apps.VSCodeLink/phenome-link-{version}.vsix')
        Readme  = @'
# Phenome Link

Phenome Link lets an AI agent read and edit the Grasshopper definition open in Rhino. Changes appear on the
canvas, and the journal records whether the agent or the user made them.

## Starting a session

1. Open Grasshopper. If no client is connected, the canvas shows a **Pair with VS Code** button in the
   lower-left area.
2. Click it. VS Code opens, installing the bundled extension if needed, and a terminal starts an agent session
   with the canvas address.
3. Describe what to build. The agent reads the canvas, makes changes, and reports results.

If the button is absent, the canvas is still available on the port in
`%TEMP%\phenome-link-<pid>.port`. `GET /` on that port documents the protocol. Any client that can make HTTP
requests can connect; the button is a convenience, not a requirement.

## Unsaved changes and the autosave copy

**New in 0.22.0: agent edits mark the document modified.** Closing Rhino now offers to save after agent
changes, and the Grasshopper title shows the unsaved-work asterisk. Before 0.22.0, link edits did not set the
modified flag, and Rhino could close without prompting and lose agent changes.

Reading does not modify the document, nor does selecting or zooming. Before an agent's first edit of a
document, the link saves an autosave copy to `%TEMP%`. This complements undo; it does not replace saving.

## Messages to the agent from the canvas

The **Phenome > Link** panel has two components: *Send to Agent* sends connected text to the journal when
`Send` is true, where the agent can read it, and *Agent Replies* returns agent responses to a connected panel.

## For the agent's benefit

In VS Code, run **Phenome Link: Teach Agents in This Workspace** once per project. It writes pairing notes to
`AGENTS.md`, installs an MCP server in `.phenome/`, registers it in `.mcp.json`, and trusts it in
`.claude/settings.local.json`. The agent then uses named tools instead of shell commands. Restart the agent
session after this because MCP servers load at session start.

**The trust rule covers the whole server.** With 53 verbs, per-tool approvals would prompt for each verb on
first use. The rule is `"allow": ["mcp__phenome"]`, permitting every verb, including verbs added in later
versions. This supersedes earlier `mcp__grasshopper` and per-verb entries; older entries remain harmless.

## When something goes wrong

Refused requests are logged locally to `%LOCALAPPDATA%\Phenome\link-friction.jsonl`: request, response, and
build. Nothing is transmitted. **Phenome Link: Report a Problem…** in VS Code assembles a readable report and
creates a mail draft for review before sending.
'@
    }
)

foreach ($package in $packages) {
    $projectPath = Join-Path $root $package.Project
    $staging = Join-Path $env:TEMP "phenome-yak-$($package.Name)"

    Write-Host "== $($package.Name) ==" -ForegroundColor Cyan

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $staging | Out-Null

    if ($From) {
        # Pack existing input files instead of building. Filter by extension so a stray .yak from an earlier run
        # cannot enter the staging folder and be mistaken for the current package.
        $inputs = Join-Path $root $From
        if (-not (Test-Path $inputs)) { $inputs = $From }
        if (-not (Test-Path $inputs)) { throw "There is no folder at $From to pack from." }

        Get-ChildItem $inputs -File |
            Where-Object { $_.Extension -in '.gha', '.dll', '.rhp', '.vsix', '.yml' } |
            Copy-Item -Destination $staging

        Write-Host "  packing what is already in $inputs"
    }
    else {
        dotnet build $projectPath -c Release --nologo | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "$($package.Name): the Release build failed."
        }

        # Copy the .gha and adjacent assemblies together. A memory-loaded multi-assembly plugin cannot resolve
        # sibling assemblies; they must ship together and load from disk.
        Get-ChildItem (Join-Path $projectPath 'bin\Release\net7.0') -File |
            Where-Object { $_.Extension -in '.gha', '.dll' } |
            Copy-Item -Destination $staging

        Copy-Item (Join-Path $projectPath 'manifest.yml') $staging
    }

    # Read the version from the staged manifest: the value is then the package version and not a working-tree
    # value. CI rejects builds whose version declarations disagree, and checking one staged declaration is enough.
    $manifest = Join-Path $staging 'manifest.yml'

    if (-not (Test-Path $manifest)) {
        throw "$($package.Name): no manifest.yml among the files to pack, and no version to pack as."
    }

    $version = (Get-Content $manifest | Select-String '^version:\s*(.+)$').Matches.Groups[1].Value.Trim()

    if (-not $version) { throw "$($package.Name): manifest.yml declares no version." }

    if ($ExpectVersion -and $version -ne $ExpectVersion) {
        throw "$($package.Name): the manifest says $version and the caller expected $ExpectVersion."
    }

    # Copy candidate files when building from source. Missing candidates are not errors here; Requires below
    # decides whether the package is complete.
    if (-not $From) {
        foreach ($pattern in $package.Sources) {
            Get-ChildItem (Join-Path $root ($pattern -replace '\{version\}', $version)) -ErrorAction SilentlyContinue |
                Select-Object -First 1 |
                Copy-Item -Destination $staging -ErrorAction SilentlyContinue
        }
    }

    # Verify required contents after staging, regardless of whether files came from a build or from -From.
    foreach ($required in $package.Requires) {
        $leaf = $required -replace '\{version\}', $version

        if (-not (Test-Path (Join-Path $staging $leaf))) {
            throw "$($package.Name): the package requires $leaf and it is not there. " +
                "Build it first - pwsh tools/build.ps1 leaves everything in dist/."
        }
    }

    # Write the package README. It is installed with the package and describes use after installation; the
    # distribution folder README describes installation before the package is used.
    if ($package.Readme) {
        Set-Content (Join-Path $staging 'README.md') $package.Readme
    }

    Push-Location $staging

    try {
        & $Yak build | Out-Null

        if ($LASTEXITCODE -ne 0) {
            throw "$($package.Name): yak build failed."
        }
    }
    finally {
        Pop-Location
    }

    # These checks belong with packaging, and every caller runs the same ones.
    $built = @(Get-ChildItem $staging -Filter '*.yak')

    if ($built.Count -eq 0) {
        throw "$($package.Name): yak produced no package."
    }

    if ($built.Count -gt 1) {
        throw "$($package.Name): more than one .yak in the staging folder: $($built.Name -join ', ')"
    }

    # Yak names the file from the manifest, and the filename shows whether the expected manifest was used. A
    # mismatch is otherwise easy to miss until the package is installed.
    $yakFile = $built[0]

    if ($yakFile.Name -notlike "*-$version-*") {
        throw "$($package.Name): yak built $($yakFile.Name), which is not version $version."
    }

    Copy-Item $yakFile.FullName $Destination -Force

    Write-Host "  $($yakFile.Name) -> $Destination"
    Get-ChildItem $staging -File | ForEach-Object { Write-Host "    contained: $($_.Name)" }
}

# Write installation instructions next to the packed packages.
@"
# Phenome packages

## Installing from this folder

1. In Rhino, open **Tools > Options > Packages** or run ``_PackageManagerSettings`` and add this folder's path
   as a package source.
2. Run ``_PackageManager``, search for **phenome-link**, install, then restart Rhino.

Everyone who can read this folder can install packages. Yak has no separate permission model.

## Installing from a single .yak file

A package source must be a local or network folder path; a web link cannot be used.

1. Place the ``.yak`` file in a local or network folder, for example ``Documents\Phenome``.
2. If it arrived by mail or download, unblock it first: right-click > Properties > tick *Unblock*.
   Windows marks downloaded files, and Grasshopper silently refuses to load blocked assemblies.
3. Add that folder as a package source, using step 1 above, then install using step 2 above.

## What comes with it

**phenome-link** includes the VS Code extension (``phenome-link-*.vsix``). The canvas's *Pair with VS Code*
button passes it to VS Code before the first pairing. No separate extension install is needed.

Then open Grasshopper and look for the *Pair with VS Code* button in the lower-left canvas area.

Packed $(Get-Date -Format 'yyyy-MM-dd HH:mm').
"@ | Set-Content (Join-Path $Destination 'README.md')

Write-Host ""
Write-Host "Done. Point Rhino's Package Manager at: $Destination" -ForegroundColor Green
