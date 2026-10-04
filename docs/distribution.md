# Distributing the link

Three parts that version together: the Grasshopper plugin (`src/Phenome.Apps.GrasshopperLink`), the Rhino
plugin (`src/Phenome.Apps.RhinoLink`) and the VS Code extension (`src/Phenome.Apps.VSCodeLink`). Neither
assembly references a Phenome library, which is what lets them ship on their own, to any Rhino user with
any agent.

They do share source: `src/Phenome.Apps.Shared` is compiled into each of them by a `<Compile Include>` glob
and is not referenced as an assembly. The property above holds exactly as before, with one self-contained
`.gha`, one self-contained `.rhp` and nothing beside them to resolve at plugin-load time. The shared folder
exists because the two halves had drifted three times: twice in advertised protocol features and once in a
port-binding fix. The README in that folder has the details and the rule about what may live there.

A fourth project, `src/Phenome.Apps.RhinoInsideLink`, does **not** version with them and is not
distributed. It starts a Rhino core of its own with no window, answers about files on disk, and is built
from source by whoever wants it. CI compiles it to keep it from going stale unnoticed, and nothing copies it
into `dist/`.

The Rhino half is easy to leave out, because everything works without it until something needs the process
half. A `.gha` only exists once Grasshopper has been started, and nothing in it can report on what happens
before that. This includes a dialog during Rhino's own startup, which holds the process while nothing is able
to report it. The `.rhp` loads at startup and answers about the process: whether the UI thread is free, what is
blocking it, and how to answer that.

## Building

```powershell
pwsh tools/build.ps1
```

The script release-builds both plugins, packages the extension, packs the Claude Desktop extension, and
leaves the `.gha`, the `.rhp`, the `.vsix`, the `.mcpb` and `manifest.yml` in `dist/`. Release builds carry no symbols and no machine paths, as configured by
`PathMap` and `DebugType=none` in `Directory.Build.props`. Both settings are there because the repository is
public.

**Six version declarations have to agree**, and CI refuses a build where they do not: `manifest.yml`, both
plugins' `<Version>`, the extension's `package.json`, the version `mcp.js` reports as its server, and the
Claude Desktop extension's `manifest.json`. On a tag, the tag is a seventh and the loudest. The check names its subjects one by one, and a new project has to
be added to it by hand. An unnamed project is not checked and ships with whatever number it happened to have;
v0.1.0 shipped a 0.17.0 `.vsix` this way.

CI also refuses a build whose version has no entry in `CHANGELOG.md`. Without the check, a release with a
missing changelog entry is indistinguishable from a release with no changes to report.

## Installing by hand

1. Copy the `.gha` into `%APPDATA%\Grasshopper\Libraries\`, then right-click it → Properties →
   **Unblock**. Windows marks files that arrived from elsewhere and Grasshopper refuses a blocked assembly
   **silently**. This is the most commonly missed step.
1. Drag the `.rhp` onto an open Rhino, or point `PlugInManager` at it. Rhino remembers what it has loaded
   between sessions, and this step is done once. Rhino writes that list only when it **closes normally**:
   a Rhino that is killed does not record that the plugin was loaded, and the plugin is absent next time
   with nothing to explain why.
2. `code --install-extension dist\phenome-link-<version>.vsix`, or let the canvas's *Pair with VS Code*
   button do it on the first pairing.
3. Restart Rhino.

## Claude Desktop

`src/Phenome.Apps.ClaudeDesktopLink` holds the manifest and the icon of a Claude Desktop extension (`.mcpb`).
`tools/pack-mcpb.ps1` packs it from `dist/`: `mcp.js` goes into `server/`, and the `.gha`, the `.rhp` and
`manifest.yml` go into `server/rhino/`. The build script and the workflow both call it, and it is the one
place that says what the extension contains. The packer is `@anthropic-ai/mcpb`, pinned.

The extension installs the plug-ins itself. When `mcp.js` starts and finds `rhino/manifest.yml` beside it,
it copies the folder to `%APPDATA%\McNeel\Rhinoceros\packages\8.0\phenome-link\<version>\` and writes the
version into `manifest.txt` one level up. This is the layout Yak leaves after an install, and the Package
Manager lists the result as an installed package. Rhino reads it only at startup. The copy that Teach Agents
writes into a workspace has no `rhino/` folder and installs nothing.

Three cases install nothing, and `sessions` reports which one applied under `plugins`. The answers that
report no session repeat it:

- An installed version equal to or newer than the bundled one stays. It may be a build installed on purpose,
  and an extension update does not take it away.
- A `.gha` copied by hand into `%APPDATA%\Grasshopper\Libraries` (or one folder below) blocks the install.
  Grasshopper would load both copies and stop on a duplicate-assembly dialog at every start, and which copy
  should go is the user's choice.
- On anything but Windows nothing is copied, and the manifest offers the extension for `win32` only.

The copy goes through a `.partial` folder renamed into place, so a failure halfway leaves no folder that looks
complete. Old version folders are not deleted, because a running Rhino may have their assemblies loaded.
`Zone.Identifier` is removed from each copied file: a `.mcpb` downloaded with a browser can carry the mark
into its contents, and Grasshopper refuses a marked assembly without a message.

An `.rhp` that was dragged onto Rhino by hand from another folder is not detected. Rhino would then hold two
records of one plug-in id, and what it does with them has not been tried.

## Yak, for a private folder

There is no Yak package on any server and no plan to put one there. `tools/pack-yak.ps1` builds one for a
folder under the builder's own control. It stages the `.gha` with every assembly beside it, adds the `.rhp`
and the `.vsix` to make one install the whole install, runs `Yak.exe build`, and copies the `.yak` to a
destination. Beside the `.yak` it writes a README that explains the folder to whoever opens it.

```powershell
pwsh tools/pack-yak.ps1                           # into dist/yak
pwsh tools/pack-yak.ps1 -Destination <folder>      # straight onto a share
pwsh tools/pack-yak.ps1 -From dist -Destination .  # pack what a build already made, without rebuilding
```

**The script is the only place that defines what a package contains.** Each package states its contents
once, as `Requires`, by the name each file has inside it. That list is checked after the staging folder is
filled, whichever way the files got there. `-From dist` packs what `tools/build.ps1` already built, without
building again. No CI job runs the script; it is run by hand.

The contents used to be defined in three places (this script, a CI yak job since removed, and
`tools/build.ps1` deciding what lands in `dist/`), and they disagreed. The script looked for the `.vsix` by wildcard in the folder that
produces it, while `build.ps1` *moves* it from there into `dist/`. Running the two in their natural order
produced a package with no extension in it, no error, and a README inside saying the pair button would
install one. The wildcard also picked by string order, in which `0.9.0` sorts above `0.22.0`. Naming what
must be there, and refusing to pack without it, fixes both.

The packer serves two ways of handing a package over:

- **A folder as a package source.** Yak has no notion of permissions, and a folder, local or on a network
  share, *is* the access control: read access is what permits install. The recipient adds the path under
  Rhino's **Tools › Options › Packages**, then installs `phenome-link` from `_PackageManager`. A SharePoint
  link does not work, because the Package Manager needs a path on the recipient's own machine or network.
- **The `.yak` file, sent to the recipient.** The recipient drops it in a folder of their own, unblocks it,
  adds that folder as a source and installs. Forwarding the file together with the README the packer writes
  is enough on its own.

**Publishing to the public Yak server** (`yak push`) would allow installing from the Package Manager with
no instructions at all, and updates would arrive the same way. It is deliberately **not done**, and not
merely postponed. A published version can only be withdrawn from the index with `yak yank`, one version at a
time, and never from the machines that already have it. A repository and a release are retractable in a way
that a package index is not.

## Releases

A tag `v<version>` is the release. Pushing one runs the workflow, which builds the three parts, checks the
version declarations against the tag, and attaches the `.gha`, the `.rhp` and the `.vsix` to a GitHub
release with installation notes in the body.

```powershell
git push origin main
git push origin v0.24.0    # after main, so the tag names a commit the remote already has
```

**A release never carries a `.yak`.** Building one needs `Yak.exe`, which ships inside Rhino and exists on no
hosted runner, and the workflow has no Yak job. The release carries the three loose files, which are enough
to install by hand. A package is built on demand with `pwsh tools/pack-yak.ps1 -From dist -Destination
<folder>`.

The SharePoint job runs on tags only. It holds the credentials for the company library, and a tag can only be
pushed by someone with write access, while a fork's pull request is never a tag.
