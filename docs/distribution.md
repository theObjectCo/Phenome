# Distributing the link

Three parts that version together: the Grasshopper plugin (`src/Phenome.Apps.GrasshopperLink`), the Rhino
plugin (`src/Phenome.Apps.RhinoLink`) and the VS Code extension (`src/Phenome.Apps.VSCodeLink`). Neither
assembly references a Phenome library, which is what lets them ship on their own, to any Rhino user with
any agent.

They do share source — `src/Phenome.Apps.Shared`, compiled into each of them by a `<Compile Include>` glob
rather than referenced as an assembly. That keeps the property above exactly as it was: one self-contained
`.gha` and one self-contained `.rhp`, with nothing beside them to resolve at plugin-load time. It exists
because the two halves had drifted three times, each time in a feature both halves' protocol text
advertised; the README in that folder has the details and the rule about what may live there.

A fourth project, `src/Phenome.Apps.RhinoInsideLink`, does **not** version with them and is not
distributed. It starts a Rhino core of its own with no window and answers about files on disk; it is built
from source by whoever wants it. CI compiles it so it cannot rot unnoticed, and nothing copies it into
`dist/`.

The Rhino half is easy to leave out, because everything works without it right up until it does not. A
`.gha` only exists once Grasshopper has been started, so nothing in it can report on what happens before
that — including a dialog during Rhino's own startup, which holds the process with nothing listening to
say so. The `.rhp` loads at startup and answers about the process: whether the UI thread is free, what is
blocking it, and how to answer that.

## Building

```powershell
pwsh tools/build.ps1
```

Release-builds both plugins, packages the extension, and leaves the `.gha`, the `.rhp`, the `.vsix` and
`manifest.yml` in `dist/`. Release builds carry no symbols and no machine paths — `PathMap` and `DebugType=none` in
`Directory.Build.props` see to that, and it matters here because this repository is public.

**Five version declarations have to agree**, and CI refuses a build where they do not: `manifest.yml`, both
plugins' `<Version>`, the extension's `package.json`, and the version `mcp.js` reports as its server. On a
tag, the tag is a sixth and the loudest. The check names its subjects one by one, so a new project has to be
added to it by hand — an unnamed one simply is not checked and ships with whatever number it happened to
have, which is how v0.1.0 came to carry a 0.17.0 `.vsix`.

CI also refuses a build whose version has no entry in `CHANGELOG.md`. A release that forgot to write one
looks exactly like a release with nothing worth saying.

## Installing by hand

1. Copy the `.gha` into `%APPDATA%\Grasshopper\Libraries\`, then right-click it → Properties →
   **Unblock**. Windows marks files that arrived from elsewhere and Grasshopper refuses a blocked assembly
   **silently** — this is the step everybody misses.
1. Drag the `.rhp` onto an open Rhino, or point `PlugInManager` at it. Rhino remembers what it has loaded
   between sessions, so this is done once — but it writes that list when it **closes normally**, so a
   Rhino killed rather than closed forgets it was ever told, and the plugin is simply absent next time
   with nothing to explain why.
2. `code --install-extension dist\phenome-link-<version>.vsix`, or let the canvas's *Pair with VS Code*
   button do it on the first pairing.
3. Restart Rhino.

## Yak, for a folder of your own

There is no Yak package on any server and no plan to put one there. `tools/pack-yak.ps1` builds one for a
folder you control: it stages the `.gha` with every assembly beside it, adds the `.rhp` and the `.vsix` so
one install is the whole install, runs `Yak.exe build`, and copies the `.yak` to a destination — with a
README written beside it for whoever finds the folder.

```powershell
pwsh tools/pack-yak.ps1                           # into dist/yak
pwsh tools/pack-yak.ps1 -Destination <folder>      # straight onto a share
pwsh tools/pack-yak.ps1 -From dist -Destination .  # pack what a build already made, without rebuilding
```

**The script is the only place that knows what a package contains.** Each package states its contents once,
as `Requires`, by the name each file has inside it; that list is checked after the staging folder is filled,
whichever way the files got there. `-From` is how CI calls it, so the yak job hands over the artefacts the
build job made and knows nothing about package contents itself.

It was three places — this script, the yak job, and `tools/build.ps1` deciding what lands in `dist/` — and
they disagreed. The script looked for the `.vsix` by wildcard in the folder that produces it, `build.ps1`
*moves* it from there into `dist/`, and running the two in their natural order made a package with no
extension in it, no complaint, and a README inside promising the pair button would install one. A wildcard
also picked by string order, where `0.9.0` sorts above `0.22.0`. Naming what must be there, and refusing
without it, closes both.

Two ways to hand it over, and the packer serves both:

- **A folder as a package source.** Yak has no notion of permissions, so a folder — local or a network
  share — *is* the access control: whoever can read it can install. The recipient adds the path under
  Rhino's **Tools › Options › Packages**, then installs `phenome-link` from `_PackageManager`. A SharePoint
  link will not do; the Package Manager wants a path on the recipient's own machine or network.
- **Send them the `.yak`.** They drop it in a folder of their own, unblock it, add that folder as a source
  and install. Forwarding the file together with the README the packer writes is enough on its own.

**Publishing to the public Yak server** (`yak push`) would let anyone install from the Package Manager with
no instructions at all, and updates would arrive the same way. It is deliberately **not done**, and not
merely postponed: a published version can only be withdrawn from the index with `yak yank`, one version at a
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

**The `.yak` is attached only sometimes, and the notes say so.** Building one needs `Yak.exe`, which ships
inside Rhino and exists on no hosted runner, so that job is gated on a self-hosted Windows runner with Rhino
and on the repository variable `PHENOME_SELF_HOSTED`. Without one the release carries the three loose files,
which is enough to install by hand. The release job tolerates the missing artefact rather than failing.

Tags only, for the yak and SharePoint jobs, and that is a security boundary rather than a convenience: this
repository is public, so an organisation runner serving it would otherwise run a stranger's pull request on
somebody's machine. A tag can only be pushed by somebody with write access; a fork's pull request never is
one.
