# Distributing the link

Two halves that version together: the Grasshopper plugin (`src/Phenome.Apps.GrasshopperLink`) and the VS
Code extension (`src/Phenome.Apps.VSCodeLink`). The plugin references no Phenome library — a single
self-contained `.gha` — which is what lets it ship on its own, to any Grasshopper user with any agent.

## Building

```powershell
pwsh tools/build.ps1
```

Release-builds the plugin, packages the extension, and leaves the `.gha`, the `.vsix` and `manifest.yml`
in `dist/`. Release builds carry no symbols and no machine paths — `PathMap` and `DebugType=none` in
`Directory.Build.props` see to that, and it matters here because this repository is public.

Keep `manifest.yml`'s version and the project's `<Version>` in step. Yak cross-checks them, and the
friction log stamps its reports with the assembly version, so a report names a build.

## Installing by hand

1. Copy the `.gha` into `%APPDATA%\Grasshopper\Libraries\`, then right-click it → Properties →
   **Unblock**. Windows marks files that arrived from elsewhere and Grasshopper refuses a blocked assembly
   **silently** — this is the step everybody misses.
2. `code --install-extension dist\phenome-link-<version>.vsix`, or let the canvas's *Pair with VS Code*
   button do it on the first pairing.
3. Restart Rhino.

## Yak, when the time comes

`tools/pack-yak.ps1` stages the `.gha` with every assembly beside it, adds the newest `phenome-link-*.vsix`
so one install is the whole install, runs `Yak.exe build`, and copies the `.yak` to a destination — with a
README written beside it for whoever finds the folder.

```powershell
pwsh tools/pack-yak.ps1                          # into dist/yak
pwsh tools/pack-yak.ps1 -Destination <folder>     # straight onto a share
```

Two ways to hand it over, and the packer serves both:

- **A folder as a package source.** Yak has no notion of permissions, so a folder — local or a network
  share — *is* the access control: whoever can read it can install. The recipient adds the path under
  Rhino's **Tools › Options › Packages**, then installs `phenome-link` from `_PackageManager`. A SharePoint
  link will not do; the Package Manager wants a path on the recipient's own machine or network.
- **Send them the `.yak`.** They drop it in a folder of their own, unblock it, add that folder as a source
  and install. Forwarding the file together with the README the packer writes is enough on its own.

**Publishing to the public Yak server** (`yak push`) would let anyone install from the Package Manager with
no instructions at all, and updates would arrive the same way. It is deliberately **not done yet**. Note
what it costs: a published version can only be withdrawn from the index with `yak yank`, one version at a
time, and never from the machines that already have it.

## Releases

There are none yet, and the README says so rather than implying otherwise. Attaching the two artefacts
`tools/build.ps1` produces to a tagged GitHub release is the natural replacement for `yak push`, and is the
next thing to build here.
