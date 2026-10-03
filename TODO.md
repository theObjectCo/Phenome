# TODO

Written 2026-08-18 from a working session with the link, and worked through on 2026-08-19. Most items came from
friction encountered while using the link, not from reading the code. The reasoning is kept so that decisions
are not reopened or dropped without record.

---

## 1. Structure

- [x] **Delete superseded files: there were none.** Every tracked file was live as of 2026-08-19: the CI
      workflow, both build scripts, both docs, the manifest, the extension, all three projects.
      `Phenome.Apps.RhinoInsideLink` was not superseded. It held one useful class plus document tooling that
      belonged in other projects. The tooling moved out, the project became the third half of the link, and
      nothing was deleted. The item is closed.

- [x] **`HeadlessRhino` is referenced, not forked.** Decided 2026-08-19: the relocated document tooling uses
      it through `ProjectReference` and keeps no copy, because the two always ship together. `HeadlessRhino`
      is therefore public API with a consumer outside this repository: `Start`, `Prepare`, `SystemDirectory`,
      `Invoke`, `Serve`, `Stop`. Renaming any of them, or making the type internal, breaks a build outside
      this solution, and nothing in this repository will report that failure. The constraint is documented
      in the csproj. The reference itself lives in the other repository.

- [x] **`tools/yak-destination.txt`: removed from the index; history left unchanged.** Decided 2026-08-19,
      after the file was untracked and ignored. The tip is clean, the file stays on disk and packing is
      unaffected, and a fresh clone falls back to `PHENOME_YAK_DESTINATION` or `dist/yak` as the script
      intends. Every commit up to that point still carries the path, and that is accepted deliberately: what
      was exposed is one folder path on one machine, while rewriting history means force-pushing over
      published commits and invalidating every clone and every release tag, which costs more than the
      exposure. If the path itself ever becomes sensitive, move the share and leave this repository's history
      as it is.

## 2. Correctness

- [x] **`param` stored a data mapping and never applied it.** Fixed 2026-08-19. The obvious cause, that the
      solution had not run yet, was ruled out: a graft answered `{ok:true}` and a second `peek` immediately
      after the first still read `count: 0`, which showed that nothing was pending.

      There were two causes, one per direction. The verb expired only the parameter, which for an **output**
      clears its data and leaves the component marked up to date. The next solution then finds nothing to do,
      and the output stays empty until the component's own input is edited. Expiring only the owner fixed the
      output but not the input: `"mapping": "graft"` appeared in `/canvas` and the component recomputed, yet
      the input still held one branch of four items, because an input keeps the volatile data it already
      collected and the mapping applies only during collection.

      The fix expires both, for different reasons: the parameter, which then collects again and maps what
      arrives, and the owner, which then recomputes over what was collected. Verified live in each direction:
      input graft gives four branches of one item and a `Result` of four sums; output graft gives the same
      downstream; `none` returns both to one branch of 30.

- [x] **One place assembles a package now.** Done 2026-08-19. There were three: `tools/pack-yak.ps1` staging
      one for a private folder source, the `yak` job in CI staging its own from `dist/`, and `tools/build.ps1`
      deciding what lands in `dist/`. The three disagreed. `pack-yak` looked for the `.vsix` by wildcard in
      the folder that produces it, `build.ps1` *moves* it from there into `dist/`, and running the two in that
      order produced a package with no extension and no error, while the README inside said the pair button
      would install one.

      `pack-yak.ps1` now holds the file list and runs the checks. The package states its contents once as
      `Requires`, verified after staging regardless of how the files arrived. `-From <folder>` packs what a
      build already produced instead of rebuilding, which is how CI calls it. The two checks that lived only
      in CI (exactly one `.yak`, and its name carrying the manifest's version) moved into the script.
      `-ExpectVersion` lets a caller assert the version, for example from a tag.

      Verified all four ways: from source and from `dist/` produce byte-identical contents; a tag disagreeing
      with the manifest is refused; and `-From` with the extension held back refuses and produces no package.

- [x] **An agent's edit marks the document modified.** Decided 2026-08-19. Before this, the link changed a
      document and Rhino could close it **without offering to save**, and the user lost an agent's work with
      no prompt. The cost, accepted deliberately, is a save prompt on close after every agent edit.

      The flag is set at fourteen call sites in the verbs and at none in the router. The router cannot do it:
      several verbs answer `200` with `ok:false` in the body (a `delete` that would sever live wires is the
      common one), and from outside a refusal cannot be distinguished from a change.

      Some verbs do not mark the document, each for a reason: `select` and `zoom` only change the view; `new`
      and `open` have nothing to lose yet; `save` clears the flag by definition; `bake`, `rhino` and `camera`
      change the Rhino document and not this one; and **`solver` is not a document setting**. It assigns the
      static `GH_Document.EnableSolutions`, which belongs to the application, is never written into a file,
      and is gone at the next restart. That was verified, not assumed.

      Three verbs mark conditionally, because for them doing nothing is a normal outcome: `arrange` when
      something moved, `signature` when a port was actually planted, `preview` when a flag actually flipped.
      All three are finishing steps run more than once, and a save prompt for a repeated no-op makes callers
      distrust the prompt.

- [x] **Confirmed 2026-10-02 that edits made while the editor is hidden did not solve, and fixed.** Hiding the
      editor disables the document it was showing (`Enabled` false), and an edit to a disabled document
      computes nothing. Showing the editor re-enables it without solving what was placed meanwhile. Every verb
      now solves through `Plumbing.Solve`, which re-enables the document first, and `canvas` reports `enabled`.
      Measured on 0.34.0-dev: `place` of a Construct Point with the editor hidden, then `peek`, gives one
      point. The original entry:

      **Confirm that edits made while the editor is hidden still solve.** On 0.24.0 (2026-08-23 and
      2026-08-24, three friction reports) objects placed while the Grasshopper editor was hidden
      (`-_Grasshopper _W _H`) were created and wired but never solved. A `set` after reopening a saved document
      behaved the same way. Showing the editor and expiring the object solved it. A retest on 0.24.1 could not
      reproduce it, but the report does not say whether the editor was hidden during the retest. No change
      since then mentions it, and the friction log has no report of it after 2026-08-24. The check takes a few
      minutes: hide the editor, `place` one Construct Point with a constant, `peek` its output. If it solves,
      close this item.

## 3. Done

### Two bugs the modified flag exposed, and one it did not

Setting the flag made two dormant faults visible. Both were in `save`, and both came from the verb writing the
archive itself instead of going through Grasshopper's own Save. That is deliberate: saving a copy somewhere
must not silently repoint the document.

- **Saving did not clear the flag.** The fault was invisible while nothing set the flag. Once the flag was
  set, saving still left Rhino offering to save, which trains users to dismiss the prompt without reading it.
  The flag is now cleared on a successful write.
- **The Grasshopper window kept saying "unnamed" after saving a new document.** It was reported from the
  field. `GH_DocumentEditor` caches its caption and rebuilds it from five places only: its own Save and Save As
  menu handlers, a canvas document swap, opening through script access, and the canvas's handler for the
  modified flag changing. Saving through the link is none of the first four, and the fifth never fired because
  the link did not touch the flag. `DisplayName` was correct throughout, and the title bar never refreshed.
  The fix calls the public `GH_Document.OnModifiedChanged()` after a save, unconditionally. The assignment
  above notifies only when the value actually changes, and saving a document with no edits would otherwise
  leave the stale title in place. Measured, the title went from `Grasshopper - unnamed` to
  `Grasshopper - title-test`.

  `GH_DocumentServer` was checked first as a possible hook, since a document server is a plausible place for a
  rename. It is not one: its whole public surface is the document list (add, remove, promote, counts, names)
  and two events, with nothing for saving, renaming or the caption.

**`arrange` is now idempotent.** `Arrange.Apply` returned 1 per object it *placed* instead of per object it
moved, while its summary said "how many objects moved". A settled document therefore still answered
`moved: 7`, and every rerun pushed an undo step per object that undid nothing. The cause was in how the
objects were counted, and the `if (count > 0)` guard above was only a downstream symptom. The fix is at the
source: an object already within half a pixel of its target position is not moved, recorded or counted. The
measurement gave `moved: 1` for the one object out of place, then `moved: 0`.

Verified end to end in a live Rhino: opening a document gives `false`; `canvas`, `wires`, `components`,
`select`, `zoom` leave it `false`; `place` makes it `true`; `save` clears it *and* fixes the title; `arrange`
marks only the run that moved something; and after `/set` the Grasshopper title reads `title-test*`, where the
asterisk shows that the document has unsaved changes.

### The structural refactor (2026-08-19)

**The shared code has one copy, in `src/Phenome.Apps.Shared/`.** It is compiled into both plugins with a
`<Compile Include>` glob and not referenced as an assembly: a single self-contained `.gha` and `.rhp` is the
point of the packaging, and a `ProjectReference` would add a third file that must resolve at plugin-load time.
The namespace is `Phenome.Apps`, the parent of both plugins' namespaces, and every call site in both halves
therefore reads `Json.Quote` and `Pulse.Report` unchanged, with no import and no qualification anywhere.
`Json`, `Pulse` and a new `Loopback` live there. The README there documents what may go in: Rhino-only code,
nothing from Grasshopper, because the Rhino half must answer about a dialog that appears before Grasshopper has
loaded.

The two copies had drifted **three** times before this change, each time in the same way: a feature advertised
in both halves' protocol text was implemented on the canvas side and missing on the Rhino side.

- `dismiss` accepted a `key` on the canvas side and ignored it on the Rhino side. An agent reading the Rhino
  copy reported a bug the canvas implementation did not have, and retracted the report.
- **`/pulse` reported `clickable` on the canvas side only.** `docs/protocol.md` documents the field, and the
  Rhino half's own `GET /` refers callers to it. The Rhino half exists for Rhino 8's Eto dialogs, which are
  the ones where `clickable` is false. The field was therefore missing in exactly the case it exists for.
- **The Rhino half still had the racing `FreePort()`** that the canvas half had fixed: probe, release, bind.
  Two Rhinos starting together could be assigned the same port, and the loser wrote a discovery file for a
  port with no listener. Now both call `Loopback.Listen(out int port)`, which binds in a retry loop and
  returns the port only once a listener is running on it. Two Rhinos starting together is the Rhino half's
  main use case, and the race was most likely to occur there.

**`LinkServer.cs`, 2999 lines and one class, became a `Bridge.Verbs` namespace of seven files.** It was first
split into partials named `LinkServer.Objects.cs` and so on; that produced redundant naming, and a folder
named after the class cannot become a namespace while the class exists. The verbs are now separate classes:
the server routes requests and the verbs carry them out. The line counts below are from the state after that
2026-08-19 split.

| file | lines | what is in it |
|---|---|---|
| `Bridge/LinkServer.cs` | 311 | the listener and the routing table, and nothing else |
| `Bridge/Verbs/Plumbing.cs` | 208 | what every verb needs |
| `Bridge/Verbs/Documents.cs` | 232 | documents, the solver, history, saving, scripts, baking |
| `Bridge/Verbs/Objects.cs` | 670 | objects, wires, values, placing |
| `Bridge/Verbs/Groups.cs` | 217 | declaring a group's signature, filling it, laying groups out |
| `Bridge/Verbs/Reading.cs` | 411 | describe, wires, peek, what is installed |
| `Bridge/Verbs/View.cs` | 410 | canvas and viewport images, preview flags, the camera |
| `Bridge/Verbs/Process.cs` | 79 | Rhino as a process: saying, dismissing, escaping, friction |

The split was cheap because the coupling was measured first. Past the shared plumbing, only six members crossed
a group boundary. `Plumbing` collects the request readers, `OnUi`, `ActiveDocument`, the autosave guard and the
four finders, and every verb file imports it with `using static`, which meant that **not one call site
changed**. Qualified call sites would have made the diff hundreds of lines of `Plumbing.` and useless for
review.

Both moves were done by a script that carries every member block verbatim and refuses to write anything unless
the assignment accounts for the class body exactly. The result was then proved, not assumed: after normalising
access modifiers and sorting, the 2398 body lines before and after differ **only** in the routing table, and
stripping the seven class prefixes from that table makes it byte-identical to the original. No verb was
silently re-pointed at another verb's handler. The compiler could not have caught that, because every handler
takes a `JsonDocument` and returns a `string`.

**Namespaces follow the folders.** `Bridge/` holds the server, journal, friction log, command-line capture and
document watcher, with `Bridge/Verbs/` inside it; `Definition/` holds the transcriber, arrange, catalogue,
scripts, signature and review; the four files Grasshopper itself loads stay at the root. The dependency edges
are one-way: `Definition` depends on nothing, `Bridge` depends on `Definition`, and the plugin surface depends
on `Bridge`. Nesting provides the rest (`Bridge.Verbs` sees `Bridge`, which sees the root, which sees
`Phenome.Apps`), and the whole move needed four `using` lines in the root files and no qualification anywhere.

Both halves build Release clean with `TreatWarningsAsErrors`, and the canvas half was verified in a live Rhino
across every verb the move touched: `new`, `group` ×2, `place` ×2, `wire`, `set`, `param`, `peek`, `describe`,
`wires`, `select`, `zoom`, `components`, `say`, `scripts`, `rhino`, `solver` ×2, `bake`, `arrange`,
`signature` ×2, `review`, `preview`, `undo`, `redo`, `delete`, `camera`, `plugins`, `screenshot`,
`canvas-image`, `console`, `pulse`, `dismiss`. The orange working border appeared, which showed the plugin
surface reaching into `Bridge` across the new namespace boundary at run time.

### The request echo, reformatted

Rhino's command line gets one line per request. Two things were wrong with that line, and both showed on a
screenful and not on a single line:

```
  02:37:43 describe       ok      0ms          02:37:43  :61957 describe          0 ms
  02:37:50 set            ok    182ms          02:37:50  :61957 set             182 ms
  02:39:21 canvas-image   ok     1.4s          02:39:21  :61957 canvas-image    1.4 s
  02:41:30 bake           ok    2m05s   -->    02:41:30  :61957 bake           2m05 s
  02:41:44 delete         FAIL    31ms  ...    02:41:44  :61957 delete           31 ms  !!  would cut 3 wires
```

- **The amount and the unit are separate columns.** `182` and `1.4` line up on their digits, and the slow
  call is found by the width of a number. Right-aligning the whole `182ms`/`1.4s` string floats the unit, and
  then nothing aligns.
- **Nothing says `ok` any more.** A column of fourteen identical words was the widest thing on the line and
  carried no information; what matters is the line that is *not* ok. Only that line is marked, with `!!`, in
  the column a reader is already scanning, and the marker column stays aligned because the unit field is a
  fixed two characters wide.
- **The port is on every line.** It never changes within a Rhino, which would argue against repeating it.
  The banner written once at load, where the port used to be, has scrolled off the top by the fifteenth
  request, and the port is the one fact a reader needs in order to hand this session to an agent or to tell
  which of two Rhinos they are looking at. A constant column costs little to scan past, and any screenshot of
  this log then carries the port. The port is written with its colon, because five bare digits beside a clock
  read as a number of unclear purpose. It is padded on the right, unlike every other number here: aligning
  digits that never vary buys nothing, and a colon in a fixed column makes every line begin with the same
  shape.

The evidence, stated exactly: the column layout was photographed on the real command line before the port was
added (`place 84 ms`, `place 342 ms`, `wire 392 ms`, digits aligned), and that photograph proves the pipeline
renders the format string faithfully in a monospace column. The port is a change to that same format string,
checked by construction. What *was* verified live afterwards is the one thing that could have broken:
`CommandLine.IsOurs` recognises the echo by its shape (two spaces, a digit, colons at 4 and 7), and after the
change `/console` still comes back empty. The echo is still filtered, and an agent is not shown its own
requests as Rhino's output.

**Superseded the same day, after reviewing a screenful of the result:**

```
[00:20:12] [127.0.0.1:53911] [78 ms] new
[00:20:26] [127.0.0.1:53911] [14 ms] place  !!  'Addition' names 2 different components
```

The new line has three bracketed fields and then the verb. It superseded two of the notes above, and both were
wrong in the same way: they reasoned from what a column *should* need and not from what this log contains.

The port became the whole address, because a line reading `127.0.0.1:53911` can be pasted into a request and
one reading `:53911` has to be assembled first. It now comes from the same constant the listener binds, and the
log cannot name an address with nothing listening on it.

The **duration also lost its padding**, which the note above argued for at length. Aligned digits are worth
having in a column of four-digit numbers; almost every line here is two digits of milliseconds, and padding to
five created a gap. The brackets already do that work: `[1.4 s]` is findable among `[78 ms]` because the edges
are drawn. The verb moved to the end for the same reason. It is the only field whose width varies and the only
one a reader scans *for*, and no column should follow it.

`IsOurs` had to change with it, because it matches the echo by shape and the shape now begins `[hh:mm:ss]`.
Without the change, the plugin's own lines would have started appearing in `/console` as though Rhino had
written them. A live check after the change showed `/console` still answering empty with the canvas busy.

### Graceful shutdown: two bugs in `Pulse`

The open question was whether closing Rhino from outside is a kill or a graceful close. It is graceful
(`CloseMainWindow()` posts `WM_CLOSE` and Rhino runs its normal shutdown), but with an unsaved definition that
shutdown stops on **Grasshopper's multi-save prompt**, and the link could not see the prompt. That happened
three times in one session, and each time clearing it needed hand-written Win32 code. There were two separate
causes, both now fixed in the shared `Pulse`, and both halves got the fix at once:

- **The whole diagnosis depended on `Process.MainWindowHandle`, asked for at the moment of the question.**
  Rhino destroys its frame *before* that prompt is answered, and from then on `MainWindowHandle` answers with
  the prompt itself, a visible, enabled window. The check "is the main window disabled" therefore said no, and
  `pulse` reported `busy, working on something unnamed` while a dialog with a Close button held the exit. The
  frame is now recorded once, from the first idle, and never refreshed. A destroyed frame stays destroyed, and
  the shutdown case needs exactly that fact. With the frame gone, no window can be owned by it, and any visible
  enabled window of the process is then the one holding the exit.
- **`ButtonsOf` compared the window class for equality with `"Button"`.** A raw Win32 button's class is
  exactly that, but any framework that superclasses it registers its own name: WinForms buttons come back as
  `WindowsForms10.Button.app.0.<hash>`, and equality misses every one. Grasshopper's own dialogs are WinForms,
  and the prompt therefore reported `buttons: []`, `clickable: false` while carrying an ordinary `Close` button,
  and `dismiss` could not answer the one dialog an agent most needs to answer. Matching is now on *containing*
  "Button", case-insensitively.

Measured before and after on the same prompt:

```
before   state: busy      dialog: { present: false }                    advice: "working on something unnamed"
after    state: blocked   dialog: { title: "Grasshopper multi-save",    advice: "The dialog ... is open."
                                    buttons: ["Close"], clickable: true }
```

After the fix, `dismiss button:"Close"` closed Rhino and the process exited: a graceful shutdown driven
entirely through the link, with no Win32 code by hand. `RhinoApp.MainWindowHandle()` would have been the
obvious source for the frame. It was rejected because it cannot be shown to be safe off the UI thread, and a
`pulse` that can block is worse than no `pulse`.

### A bug the refactor's own verification found

**A group at the end of a definition reported no outlets, and `preview` darkened the product.**
`Signature.Ports` decided an outlet by "has a recipient outside the group", and the terminal group has no
recipients anywhere, because it *is* the answer. Both consequences were reproduced. `peek` on that group
answered `outlets: []` and hid the values worth reading, and the whole-document `preview` sweep, whose one job
is to leave "the outlets of the red and yellow groups" drawing, hid every object in it.

The root cause was one layer down: **the `group` verb created ports without the mark `signature` uses**. A
port a caller had asked for *by name* was therefore indistinguishable from any parameter standing in the group,
and was recognised only while a wire happened to cross the boundary at it. `signature` could also create a
duplicate port in front of a declared one, which is the duplication its own comments warn about. The fix marks
ports wherever they are created (`Signature.MarkAsPort`) and counts a marked port fed from inside with nothing
downstream as an outlet. After the fix the terminal group answers
`ball / Brep / 1 item / "Untrimmed Surface"`, the sweep leaves it drawing, the sphere is on screen, and two
`signature` calls in a row still add nothing.

### Behaviour fixes, all built and smoke-tested against a live Rhino

- The friction log was losing entries with two Rhinos open. It was one machine-wide file guarded only by an
  in-process lock, with a read-halve-rewrite. It now uses a named mutex (`Local\PhenomeLinkFriction`) and
  reads through `FileShare.ReadWrite`, because `File.ReadAllLines` demanded exclusivity and threw whenever
  another instance was mid-append.
- `preview` reported a delta instead of a state: `drawing` was `on ? 0 : count`, and restoring therefore
  always answered zero, which reads as failure. This caused a long incorrect diagnosis. It now reports
  `hidden`, `drawing` and `changed`.
- `bake` was a silent no-op that answered `{ok:true, baked:0}` with no reason. It now returns a `skipped`
  array distinguishing not-on-canvas / not-bakeable / nothing-to-bake-now / produced-nothing.
- `rhino` returned a bare `{ok:false}`. Rhino gives only a bool, and the verb now names the usual causes and
  points at `/console`, `/pulse` and `/escape`.
- `console` discarded the link's own lines, and the link's own faults could not be read through the link.
  `?mine=true` was added, with its own ring.
- `place` left orphans: atomicity covered proxy resolution but not parameter names, which are checked in the
  wiring pass after every object is added. A misspelt input left seven objects standing. `place` now rolls
  back in reverse on any failure.
- `FromRhinoLink` built a new `HttpClient` per call and now uses one static client.
- `describe` reports `enabled`, `drawing` and the component's own runtime messages. That was exactly the
  information missing when a group stopped solving.
- **Stale files are removed on start, because exit does not always happen.** Any `phenome-*-<pid>.port` whose
  process is gone is removed, along with autosaves older than a week. In the verification, six port files
  became one and fifty autosaves became thirteen. Errors are ignored deliberately: a link that fails to start
  over another process's leftover file would be a far worse trade.
- `plugins` reports `shipped` correctly and now also reports `rhinoRoot`, the prefix the flag is decided
  against; without it a reader could not see why something was or was not marked. Finding the root by
  counting directory levels up from RhinoCommon was wrong: RhinoCommon sits in `System` for the .NET Framework
  load and in `System\netcore` for .NET 7, and one hop landed on `System`, where no plug-in path matched. It
  now walks up to whichever directory holds `Plug-ins`. After the fix `rhinoRoot` resolves to
  `C:\Program Files\Rhino 8`, `GhPython.gha` flips to shipped, and nothing under Program Files is left marked
  otherwise.
- `/canvas` reports `modified` and `path` on the document.

### New verbs

- **`camera`** reads or aims the active viewport: projection, location, target, up, 35 mm lens, viewport
  pixel size. Rhino's `Zoom` is interactive, and scripting it waits for a pick that never comes and holds the
  UI thread, and from then on every verb reports "busy" although waiting has no effect.
- **`escape`** posts Escape to the focused window and cancels whatever Rhino is waiting for. It was verified
  on two different hangs:
  - a scripted interactive command: `Zoom` had been busy for 26 s, and one call made Rhino idle in 44 ms.
    `dialog.present` was `false` throughout, and `dismiss` could not have answered it at all;
  - a modal save prompt that `pulse` reported as `buttons: []`, `clickable: false`. Rhino 8's own dialogs
    draw their buttons, which leaves nothing to post a click to, and only a key reaches them. One call took
    Rhino from blocked to idle.
- **`plugins`** lists Grasshopper libraries and loaded Rhino plug-ins, with version and origin. It was added
  because a console message named a plug-in, and attributing it required starting a second Rhino to reproduce
  the fault.
- **`sessions` with `use` / `release`** pins a canvas session. With two Rhinos open, the choice was
  previously made by whichever answered first, and an agent could end up editing the canvas it was not using.

## 4. Not a problem, checked

- **Yak's `Content name doesn't match manifest: 'Phenome.Apps.RhinoLink' != 'phenome-link'` is structural.**
  This was checked by opening the built package: all four files are inside, `.rhp` included, and nothing is
  dropped. Yak reads each content assembly's plugin name and compares it with the manifest's `name`. The
  `.gha` matches (`LinkLibrary.Name` is "Phenome Link", which normalises to `phenome-link`). A package
  carrying two plugins can match at most one of them, and short of splitting the package a warning is the
  only possible outcome. Splitting would defeat the purpose: they version together, and the half that reports
  on a stuck Rhino is of no use uninstalled.

  The warning still led to an inspection that found a separate problem beside it: Rhino's PlugInManager
  showed `Phenome.Apps.RhinoLink`. `PlugIn.PlugInNameFromAssembly` reads the assembly's `Title` attribute and
  falls back to the assembly name (confirmed in RhinoCommon's IL, which calls `GetCustomAttributes`, then
  `get_Title`, then `GetName`). Nothing set a title, and the user saw the fallback. The title is now
  `Phenome Rhino Link`, the name the plugin already gives itself when it addresses a person: `OnLoad` says
  "Phenome Rhino Link did not start".

  The first draft of this note had the next point wrong. Yak's warning is unchanged by the new title, which
  shows that Yak reads the assembly or file name and not the title. The two names come from different places,
  and only one of them is what a person reads.

- **`CommandLine` stays in two copies, deliberately.** It looks like the same duplication as `Pulse` and is a
  different case: the Rhino half is the only drain of Rhino's capture buffer, because
  `CapturedCommandWindowStrings` clears as it reads and two readers halve each other's lines. The canvas half
  detects that and reads over loopback instead, keeps a second ring for the link's own output, and filters a
  request echo the Rhino half never writes. The two copies do different jobs and share a ring and a JSON
  writer, and merging them would need a base class whose cost exceeds its benefit. This is recorded so that a
  later cleanup does not merge them.

- **`manifest.yml` belongs inside the project it describes.** It looked misplaced, since it describes a
  package spanning three projects while sitting in the Grasshopper one. `tools/pack-yak.ps1`, however, is
  built around a `$packages` list where each entry names a `Project`, and it finds that package's manifest by
  that path. The script's current comment says the components plugin has a manifest but is not distributed
  yet and should be added to this list when packaging starts. The design is one manifest per package, keyed
  by project, and moving it to `src/` would break the second package before it exists.

- `manifest.yml` appears in both `src/Phenome.Apps.GrasshopperLink/` and `dist/` and the two are
  byte-identical, but `dist/` is gitignored and untracked, and `tools/build.ps1` copies the file there as a
  build step. The repository holds one manifest, and the copy in `dist/` was merely older than the binaries
  beside it.

- **`docs/protocol.md` was already right about `clickable`.** The documentation described the field
  correctly, in the section that covers both halves, and the drift was in one of the two implementations,
  which did not have the field.

- **CI and both build scripts survived the file moves untouched.** They address projects and build outputs
  and never individual sources, and the new `Bridge/` and `Definition/` folders needed no change in them.
  Because the shared folder has no `.csproj`, it never becomes a project of its own.

## 5. Known, and left for later on purpose

- [x] **Done 2026-10-02: `arrange` reserves room for captions.** A group block's size carries a band for its
      own captions and the width of the widest one, the body is applied below the band, and Captions stacks
      the notes into it in whole pixels. Measured on three mother groups with captions at every level and on a
      caption wider than its group beside a neighbour, both cases went from overlapping frames to none, and
      arrange run three times answers 17, 0, 0. The original entry:

      **`arrange` does not reserve room for a caption, and a long note can push two group frames together.**
      Found 2026-08-20 while checking that captions land where they should. They do, and this is the other
      half of the same feature request: *"it must reserve space so an annotation's bbox never intersects
      another object's."* Idempotence is done and proven; reservation is not.

      `arrange` measures a group's box from its **nodes**. A note is not a node: it carries no data, has no
      ports and takes part in no dataflow, and it was therefore kept out of the layout algebra. The caption
      pass then places each note above the members it captions. A scribble is one unwrapped line, and a wordy
      caption is easily wider than the four sliders under it. The frame is drawn around every member including
      the note and grows past the width the layout reserved, and the next block along, placed at `BlockGapX`
      from a box narrower than the frame, touches it. On the caption test, a 503 px caption on a block at
      x=100 reaches x=603, and the neighbouring group starts at x=579.

      Today it costs nothing blocking: `review` reports it as `polish`, the definition runs, and the canvas
      reads fine unless the captions are long. The one real harm was the advice. The finding said "run
      arrange", which cannot help, because arrange has already placed the blocks where it intends to. That is
      fixed: the finding now compares the two groups' **bodies** as well as their frames, and when only the
      frames touch it says a note is reaching past what it captions and should be shortened. Accurate advice
      for a cosmetic fault is the right trade before a release.

      The real fix, when it is worth it, folds the caption band into `Measure` (a group block's width becomes
      `max(body, widest caption)` and its height gains the caption band) and offsets its children downward in
      `Apply` by that band. The reserved box is then the box that gets drawn. This is deliberately **not**
      done now. `Measure`/`Apply` is the most delicate code in the repo, and `Attributes.Bounds` is computed
      during a layout pass and cached: anything that measures right after writing a pivot reads a stale
      number, a problem already encountered three times earlier in this file. The reward is that two frames
      stop touching. It is the wrong trade during release week and the right trade afterwards.
