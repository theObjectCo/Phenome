# Packs both Grasshopper plugins for a private Yak source.
#
# A private source is simply a folder full of .yak files that a Rhino adds as a package source: whoever can
# read the folder can install, whoever cannot, cannot. That is the whole of the access control, and it is
# why this exists - Yak itself has no notion of who may install what.
#
#   pwsh tools/pack-yak.ps1                        # to the remembered destination, or dist/yak
#   pwsh tools/pack-yak.ps1 -Destination <folder>  # somewhere else, this once
#
# Where "remembered" is tools/yak-destination.txt (gitignored) or the PHENOME_YAK_DESTINATION variable:
# the share is somebody's own path, and a path with a person's name in it does not belong in a repository.
#
# The link package carries the VS Code extension beside its .gha, so one install is the whole install: the
# pair button hands the vsix to VS Code before the first pairing.

[CmdletBinding()]
param(
    [string] $Destination,
    [string] $Yak = 'C:\Program Files\Rhino 8\System\Yak.exe'
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

# For now the link travels alone. The components plugin carries the kernel and is not distributed yet -
# its manifest is written and this list is where it joins, when it is time.
$packages = @(
    @{
        Name    = 'phenome-link'
        Project = 'src/Phenome.Apps.GrasshopperLink'
        Extras  = @('src/Phenome.Apps.VSCodeLink/phenome-link-*.vsix')
        Readme  = @'
# Phenome Link

Your Grasshopper canvas, over loopback HTTP, so an AI agent can work on it beside you: it sees what you
see, edits what you edit, and every change either of you makes is journalled with a name against it.

## Starting a session

1. Open Grasshopper. Bottom-left of the canvas is a **Pair with VS Code** button - it shows while nobody
   is connected.
2. Click it. VS Code opens (installing the extension carried in this package if it is not there yet) and
   a terminal starts an agent session, already told where the canvas is.
3. Say what you want built. The agent reads the canvas, builds, and talks back.

No button? Nothing is lost: the canvas answers on the port written in
`%TEMP%\phenome-link-<pid>.port`, and `GET /` on it describes the whole protocol. Any agent that can make
an HTTP request is a peer here - the pairing button is a shortcut, not a requirement.

## Talking to your agent from the canvas

The **Phenome > Link** panel has two components: *Send to Agent* (wire a button to it and your text goes
into the journal, where the agent reads it) and *Agent Replies* (wire a panel to it to read what came
back).

## For the agent's benefit

In VS Code, run **Phenome Link: Teach Agents in This Workspace** once per project. It writes the pairing
notes into `AGENTS.md`, plants an MCP server in `.phenome/`, registers it in `.mcp.json` and trusts it in
`.claude/settings.local.json` - after which the agent has named tools instead of shell commands, and asks
permission once instead of every call. Restart the agent session afterwards: MCP servers load at session
start.

## When something goes wrong

Refused requests are logged locally to `%LOCALAPPDATA%\Phenome\link-friction.jsonl` - what was asked, what
was said back, which build. Nothing is sent anywhere. **Phenome Link: Report a Problem…** in VS Code
assembles that into one readable file and offers a mail draft you send yourself, after reading it.
'@
    }
)

foreach ($package in $packages) {
    $projectPath = Join-Path $root $package.Project
    $staging = Join-Path $env:TEMP "phenome-yak-$($package.Name)"

    Write-Host "== $($package.Name) ==" -ForegroundColor Cyan

    dotnet build $projectPath -c Release --nologo | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "$($package.Name): the Release build failed."
    }

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $staging | Out-Null

    # The .gha and every assembly beside it: a memory-loaded multi-assembly plugin cannot resolve its
    # siblings, so they travel together and load from disk.
    $built = Join-Path $projectPath 'bin\Release\net7.0'

    Get-ChildItem $built -File |
        Where-Object { $_.Extension -in '.gha', '.dll' } |
        Copy-Item -Destination $staging

    Copy-Item (Join-Path $projectPath 'manifest.yml') $staging

    foreach ($pattern in $package.Extras) {
        Get-ChildItem (Join-Path $root $pattern) -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            Select-Object -First 1 |
            Copy-Item -Destination $staging
    }

    # Travels inside the package and lands in the installed folder: the guide for after the install, as
    # opposed to the one in the distribution folder, which is the guide for before it.
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

    $yakFile = Get-ChildItem $staging -Filter '*.yak' | Select-Object -First 1

    if (-not $yakFile) {
        throw "$($package.Name): yak produced no package."
    }

    Copy-Item $yakFile.FullName $Destination -Force

    Write-Host "  $($yakFile.Name) -> $Destination"
    Get-ChildItem $staging -File | ForEach-Object { Write-Host "    contained: $($_.Name)" }
}

# The note that turns a folder into instructions, refreshed on every pack.
@"
# Phenome packages

## If you can see this folder

1. Rhino: **Tools > Options > Packages** (or run ``_PackageManagerSettings``) and add this folder's path
   as a source.
2. Run ``_PackageManager``, search for **phenome-link**, install, restart Rhino.

Whoever can read this folder can install; whoever cannot, cannot. That is the access control - Yak has no
notion of permissions of its own.

## If somebody sent you the .yak file

A package source has to be a folder on your own machine or network - a web link will not do. So:

1. Put the ``.yak`` file in any folder of your own, e.g. ``Documents\Phenome``.
2. **Unblock it first** if it arrived by mail or download: right-click > Properties > tick *Unblock*.
   Windows marks downloaded files, and Grasshopper refuses to load a blocked assembly - silently.
3. Add that folder as a package source (step 1 above) and install (step 2 above).

## What comes with it

**phenome-link** carries the VS Code extension (``phenome-link-*.vsix``) inside the package. The canvas's
*Pair with VS Code* button hands it to VS Code before the first pairing, so there is nothing else to
install by hand.

Then: open Grasshopper, look for the *Pair with VS Code* button in the bottom-left of the canvas.

Packed $(Get-Date -Format 'yyyy-MM-dd HH:mm').
"@ | Set-Content (Join-Path $Destination 'README.md')

Write-Host ""
Write-Host "Done. Point Rhino's Package Manager at: $Destination" -ForegroundColor Green
