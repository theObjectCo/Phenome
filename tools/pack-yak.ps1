# Packs both Grasshopper plugins for a private Yak source.
#
# A private source is simply a folder full of .yak files that a Rhino adds as a package source: whoever can
# read the folder can install, whoever cannot, cannot. That is the whole of the access control, and it is
# why this exists - Yak itself has no notion of who may install what.
#
#   pwsh tools/pack-yak.ps1                        # to the remembered destination, or dist/yak
#   pwsh tools/pack-yak.ps1 -Destination <folder>  # somewhere else, this once
#   pwsh tools/pack-yak.ps1 -From dist             # pack what a build already produced, without rebuilding
#
# Where "remembered" is tools/yak-destination.txt (gitignored) or the PHENOME_YAK_DESTINATION variable:
# the share is somebody's own path, and a path with a person's name in it does not belong in a repository.
#
# The link package carries the VS Code extension beside its .gha, so one install is the whole install: the
# pair button hands the vsix to VS Code before the first pairing.
#
# -From exists so that this is the only place that knows what a package contains. It used to be three: this
# script, the yak job in CI, and tools/build.ps1 deciding what landed in dist. Three descriptions of one thing
# disagree eventually, and this set did - see the note on $packages below. CI now calls this with -From dist,
# so the list of what a package must hold is stated once and checked the same way however the files got there.

[CmdletBinding()]
param(
    [string] $Destination,
    [string] $Yak = 'C:\Program Files\Rhino 8\System\Yak.exe',

    # A folder holding what a build already produced. Given, nothing is rebuilt and these files are the
    # package's contents; omitted, the projects are built and their outputs gathered.
    [string] $From,

    # The version the package is expected to carry, when the caller has an opinion of its own - a tag, in CI.
    # Checked against the name Yak gives the file, which is the evidence that the right manifest was used.
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

# For now the link travels alone. The components plugin carries the kernel and is not distributed yet -
# its manifest is written and this list is where it joins, when it is time.
$packages = @(
    @{
        Name    = 'phenome-link'
        Project = 'src/Phenome.Apps.GrasshopperLink'
        # What the package contains, by the name each file has inside it. This is the single description the
        # comment at the top is about: it is checked after the staging folder is filled, whether the files were
        # built here or handed over with -From, so the two ways of making a package cannot disagree about what
        # one is. {version} is filled from the manifest.
        #
        # Worth stating why it is a list of demands rather than a list of places. The extension used to be
        # looked for by wildcard in the folder that packages it, and tools/build.ps1 *moves* it out of there
        # into dist/ - so running the two in their natural order produced a package with no .vsix in it, no
        # complaint, and a README inside promising the pair button would install one. A wildcard also picks by
        # string order, where 0.9.0 sorts above 0.22.0, so a leftover from an older version would have won over
        # the current build. Naming what must be there, and refusing without it, closes both.
        #
        # The Rhino plugin is in this list because it travels in the same package as the canvas one: they
        # version together, and the half that reports on a stuck Rhino is no use sitting on a disk uninstalled.
        Requires = @(
            'Phenome.Apps.GrasshopperLink.gha',
            'Phenome.Apps.RhinoLink.rhp',
            'phenome-link-{version}.vsix',
            'manifest.yml')

        # Where to find each of those when building from source. Several entries may name the same file: the
        # extension sits in dist/ after a full build and beside its own project after a bare vsce run, and
        # either will do - so these are candidates, and Requires is what decides whether enough turned up.
        Sources  = @(
            'src/Phenome.Apps.RhinoLink/bin/Release/net7.0/Phenome.Apps.RhinoLink.rhp',
            'dist/phenome-link-{version}.vsix',
            'src/Phenome.Apps.VSCodeLink/phenome-link-{version}.vsix')
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

    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force $staging | Out-Null

    if ($From) {
        # Handed over rather than built. Named extensions rather than everything in the folder, so a stray
        # .yak from an earlier run cannot join the staging folder and then be mistaken for the one just built.
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

        # The .gha and every assembly beside it: a memory-loaded multi-assembly plugin cannot resolve its
        # siblings, so they travel together and load from disk.
        Get-ChildItem (Join-Path $projectPath 'bin\Release\net7.0') -File |
            Where-Object { $_.Extension -in '.gha', '.dll' } |
            Copy-Item -Destination $staging

        Copy-Item (Join-Path $projectPath 'manifest.yml') $staging
    }

    # The version the manifest declares, read from the staging folder so it is the one about to be packed
    # rather than the one in the working tree. CI refuses a build where the five declarations disagree, so
    # reading any one of them reads all of them.
    $manifest = Join-Path $staging 'manifest.yml'

    if (-not (Test-Path $manifest)) {
        throw "$($package.Name): no manifest.yml among the files to pack, so there is no version to pack as."
    }

    $version = (Get-Content $manifest | Select-String '^version:\s*(.+)$').Matches.Groups[1].Value.Trim()

    if (-not $version) { throw "$($package.Name): manifest.yml declares no version." }

    if ($ExpectVersion -and $version -ne $ExpectVersion) {
        throw "$($package.Name): the manifest says $version and the caller expected $ExpectVersion."
    }

    # Candidates, when building from source. Missing ones are not an error here; Requires below decides.
    if (-not $From) {
        foreach ($pattern in $package.Sources) {
            Get-ChildItem (Join-Path $root ($pattern -replace '\{version\}', $version)) -ErrorAction SilentlyContinue |
                Select-Object -First 1 |
                Copy-Item -Destination $staging -ErrorAction SilentlyContinue
        }
    }

    # And the one check that matters, run the same way whichever road the files came by.
    foreach ($required in $package.Requires) {
        $leaf = $required -replace '\{version\}', $version

        if (-not (Test-Path (Join-Path $staging $leaf))) {
            throw "$($package.Name): the package promises $leaf and it is not there. " +
                "Build it first - pwsh tools/build.ps1 leaves everything in dist/."
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

    # Both of these came from the yak job in CI, which used to do its own staging and its own checking. They
    # belong wherever the packing happens rather than beside one caller of it.
    $built = @(Get-ChildItem $staging -Filter '*.yak')

    if ($built.Count -eq 0) {
        throw "$($package.Name): yak produced no package."
    }

    if ($built.Count -gt 1) {
        throw "$($package.Name): more than one .yak in the staging folder: $($built.Name -join ', ')"
    }

    # Yak names the file from the manifest, so the name is the evidence that the right manifest was used.
    # Checked rather than assumed, because the wrong one is not obvious until somebody installs it.
    $yakFile = $built[0]

    if ($yakFile.Name -notlike "*-$version-*") {
        throw "$($package.Name): yak built $($yakFile.Name), which is not version $version."
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
