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
        # The Rhino plugin travels in the same package as the canvas one: they version together, and the
        # half that reports on a stuck Rhino is no use sitting on somebody's disk uninstalled.
        #
        # {version} is filled in from the manifest below, and every entry here must match something or this
        # script stops. Both of those are the same lesson, learnt by shipping it wrong: the extension was
        # looked for by wildcard in the folder that packages it, and tools/build.ps1 *moves* it out of there
        # into dist/ - so running the two in their natural order produced a package with no .vsix in it, no
        # complaint, and a README inside promising the pair button would install one. A wildcard also picks by
        # string order, where 0.9.0 sorts above 0.22.0, so a leftover from an older version would have been
        # chosen over the current build. Naming the version and demanding a hit closes both.
        Extras  = @(
            'src/Phenome.Apps.RhinoLink/bin/Release/net7.0/Phenome.Apps.RhinoLink.rhp',
            'dist/phenome-link-{version}.vsix',
            'src/Phenome.Apps.VSCodeLink/phenome-link-{version}.vsix')
        # Where more than one of the patterns above may legitimately be the same file, say so: the extension
        # is in dist/ after a full build and beside its own project after a bare vsce run, and either will do.
        OneOf   = @('phenome-link-{version}.vsix')
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

## Your document stays yours

**New in 0.22.0: an agent's edit marks the document modified.** So when you close Rhino it offers to save,
the same as it would for your own edits, and the Grasshopper title carries the usual asterisk while there is
work outstanding. Before this the link changed a document and left the flag alone, which meant Rhino closed
it without asking and an agent's work could disappear with no prompt at all.

Reading never marks it, and neither does selecting or zooming. There is also an autosave into `%TEMP%` before
an agent's first edit of any document — a net under the undo stack, not a substitute for saving.

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

    # The version the manifest declares, which is the one the whole release agrees on - CI refuses a build
    # where the five declarations disagree, so reading any one of them reads all of them.
    $version = (Get-Content (Join-Path $projectPath 'manifest.yml') |
        Select-String '^version:\s*(.+)$').Matches.Groups[1].Value.Trim()

    if (-not $version) { throw "$($package.Name): manifest.yml declares no version." }

    $satisfied = @()

    foreach ($pattern in $package.Extras) {
        $filled = $pattern -replace '\{version\}', $version
        $found = Get-ChildItem (Join-Path $root $filled) -ErrorAction SilentlyContinue |
            Select-Object -First 1

        if ($found) {
            Copy-Item $found.FullName -Destination $staging
            $satisfied += Split-Path $filled -Leaf
            continue
        }

        # Alternatives are allowed to miss; a plain entry is not. A package that quietly ships without
        # something it promises is worse than one that refuses to be built.
        $leaf = Split-Path $filled -Leaf
        $alternative = $package.OneOf | ForEach-Object { $_ -replace '\{version\}', $version } | Where-Object { $_ -eq $leaf }

        if (-not $alternative) {
            throw "$($package.Name): nothing at $filled, and the package promises it."
        }
    }

    foreach ($group in @($package.OneOf)) {
        $leaf = $group -replace '\{version\}', $version
        if ($satisfied -notcontains $leaf) {
            throw "$($package.Name): $leaf was not found in any of the places it is looked for. " +
                "Build it first - pwsh tools/build.ps1 leaves it in dist/."
        }
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
