# TODO

Written 2026-08-18 from a long session using the link in earnest, and worked through on 2026-08-19 — most
of these are friction that was actually hit, not review-by-reading. The reasoning is kept, because a TODO
without its *why* gets re-litigated or quietly dropped.

---

## 1. Structure

- [x] **Delete superseded files: there were none.** Every tracked file is live — the CI workflow, both build
      scripts, both docs, the manifest, the extension, all three projects. What looked like the answer turned
      out to be a different question: `Phenome.Apps.RhinoInsideLink` was not superseded, it was a project
      holding one useful class and a pile of document tooling that belonged elsewhere. The tooling left, the
      project became the third half of the link, and nothing was deleted. Closed.

- [x] **`HeadlessRhino` is referenced, not forked.** Decided 2026-08-19: the relocated document tooling takes
      it from this project by `ProjectReference` rather than carrying a copy, on the grounds that the two always
      travel together anyway. Which makes `HeadlessRhino` public surface with a consumer outside this
      repository — `Start`, `Prepare`, `SystemDirectory`, `Invoke`, `Serve`, `Stop` — and renaming any of them,
      or making the type internal, breaks a build that is not in this solution and will not say so here. The
      csproj says as much where somebody about to do it would look. The reference itself lives in the other
      repository and is theirs to wire.

- [x] **`tools/yak-destination.txt`: out of the index, and the history is left alone.** Decided 2026-08-19,
      after the file was untracked and ignored. The tip is clean, the file stays on disk so packing is
      unaffected, and a fresh clone falls back to `PHENOME_YAK_DESTINATION` or `dist/yak` as the script
      intends. Every commit up to that point still carries the path, and that is accepted rather than
      overlooked: what leaked is one folder path on one machine, while rewriting it means force-pushing over
      published history and invalidating every clone and every release tag's commit. The cure is worse than a
      folder name. Closed so it does not get re-litigated — if the path itself ever becomes sensitive, the
      answer is to move the share, not to rewrite this repository.

## 2. Correctness

- [x] **`param` stored a data mapping and never applied it.** Fixed 2026-08-19, and the diagnosis was worth
      the two experiments it took, because the obvious reading was wrong.

      The first guess was that the solution simply had not run yet — a graft answered `{ok:true}` and the next
      `peek` read `count: 0`. It was not that: a second `peek` moments later still read zero, so nothing was
      pending. The verb expired the parameter, which for an **output** clears its data and leaves the
      component looking up to date, so the next solution finds nothing to do and the output stays empty for
      good. Only a later edit to the component's own input brought the grafted tree through.

      Expiring the owner instead fixed the output and broke the input, which is the second experiment:
      `"mapping": "graft"` showed up in `/canvas`, the component recomputed, and the input still held one
      branch of four items. An input keeps the volatile data it already collected, so the mapping was stored
      and never applied on the way in.

      So both, and for different reasons: the parameter, so it collects again and maps what arrives, and the
      owner, so it computes again over what it got. Verified live in each direction — input graft gives four
      branches of one item and a `Result` of four sums; output graft the same downstream; `none` puts both
      back to one branch of 30.

- [x] **One place assembles a package now.** Done 2026-08-19. It was three: `tools/pack-yak.ps1` staging one
      for a private folder source, the `yak` job in CI staging its own from `dist/`, and `tools/build.ps1`
      deciding what lands in `dist/`. Three descriptions of one thing disagree eventually, and these did —
      pack-yak looked for the `.vsix` by wildcard in the folder that produces it, `build.ps1` *moves* it from
      there into `dist/`, and running the two in their natural order made a package with no extension in it and
      no complaint, while the README inside promised the pair button would install one.

      `pack-yak.ps1` now owns both the list and the checks. A package states what it contains once, as
      `Requires`, and that is verified after staging however the files arrived; `-From <folder>` packs what a
      build already produced instead of rebuilding, which is how CI calls it. The two checks that lived only in
      CI — exactly one `.yak`, and its name carrying the manifest's version — moved into the script, where the
      packing is. `-ExpectVersion` lets a caller with an opinion, a tag, say so.

      Verified all four ways: from source and from `dist/` produce byte-identical contents; a tag disagreeing
      with the manifest is refused; and `-From` with the extension held back refuses and produces no package.

- [x] **An agent's edit marks the document modified.** Decided 2026-08-19: an edit is an edit. Before this,
      the link mutated a document and Rhino closed it **without offering to save** — the human lost an
      agent's work with no prompt at all. The cost, accepted deliberately, is that every agent edit now
      produces a save prompt on close.

      Fourteen call sites, in the verbs rather than in the router. The router cannot do it: several verbs
      answer `200` with `ok:false` in the body — a `delete` that would sever live wires is the common one —
      so from outside there is no way to tell a refusal from a change.

      Not marked, each for a reason: `select` and `zoom` are ways of looking; `new` and `open` have nothing
      yet to lose; `save` clears the flag by definition; `bake`, `rhino` and `camera` change the Rhino
      document and not this one; and **`solver` looks like a document setting and is not** — it assigns the
      static `GH_Document.EnableSolutions`, which belongs to the application, is never written into a file
      and is gone at the next restart. Worth checking rather than assuming, which is how that one was caught.

      Three mark conditionally, because for them doing nothing is a normal outcome: `arrange` when something
      moved, `signature` when a port was actually planted, `preview` when a flag actually flipped. All three
      are finishing moves people run more than once, and a save prompt for having run one twice teaches
      callers to distrust the prompt.

## 3. Done

### Two bugs the modified flag exposed, and one it did not

Setting the flag turned two dormant faults into visible ones, both in `save`, both because the verb writes
the archive itself rather than going through Grasshopper's own Save — deliberately, so that saving a copy
somewhere does not silently repoint the document.

- **Saving did not clear the flag.** Invisible while nothing ever set it. With the flag live, you saved and
  Rhino still offered to save — which is precisely how people learn to dismiss that prompt without reading
  it. Now cleared on a successful write.
- **The Grasshopper window kept saying "unnamed" after saving a new document.** Reported from the field, and
  the cause is one layer past where it looks: `GH_DocumentEditor` caches its caption and rebuilds it from
  five places only — its own Save and Save As menu handlers, a canvas document swap, opening through script
  access, and the canvas's handler for the modified flag changing. Saving through the link is none of the
  first four, and the fifth never fired because nothing here touched the flag. So `DisplayName` was correct
  the whole time and the title bar simply never asked it again. Fixed by calling the public
  `GH_Document.OnModifiedChanged()` after a save — unconditionally, not leaning on the assignment above,
  which only notifies when the value actually changes: saving a document that had no edits would otherwise
  leave the stale title exactly as it was. Measured: `Grasshopper - unnamed` → `Grasshopper - title-test`.

  Checked first whether `GH_DocumentServer` was the right hook, since a document server is where a rename
  would plausibly live. It is not: its whole public surface is the document list — add, remove, promote,
  counts, names, and two events. No save, no rename, no caption.

**`arrange` is idempotent now**, which it was not. `Arrange.Apply` returned 1 per object it *placed*, not
per object it moved, and its own summary said "how many objects moved" — so a settled document still
answered `moved: 7`, and every rerun pushed an undo step per object that undid nothing. Found while writing
the conditional marking above: the guard read `if (count > 0)` with a comment claiming "only when something
actually moved", which was simply false, and a wrong comment is worse than no guard. Fixed at the source —
an object already within half a pixel of its slot is not moved, is not recorded, and is not counted.
Measured: `moved: 1` for the one object out of place, then `moved: 0`.

Verified end to end in a live Rhino: opened document `false`; `canvas`, `wires`, `components`, `select`,
`zoom` leave it `false`; `place` makes it `true`; `save` clears it *and* fixes the title; `arrange` marks
only the run that moved something; and after `/set` the Grasshopper title reads `title-test*` — the
asterisk, in the window, from an agent's edit, which is the whole point.

### The structural refactor (2026-08-19)

**One copy of the shared code, in `src/Phenome.Apps.Shared/`.** Compiled into both plugins with a
`<Compile Include>` glob rather than referenced as an assembly: a single self-contained `.gha` and `.rhp`
is the point of the packaging, and a `ProjectReference` would put a third file beside them that has to
resolve at plugin-load time. The namespace is `Phenome.Apps` — the parent of both plugins' namespaces —
so every call site in both halves reads `Json.Quote` and `Pulse.Report` unchanged, with no import and no
qualification anywhere. `Json`, `Pulse` and a new `Loopback` live there. The README beside them says what
may and may not go in: Rhino-only, nothing from Grasshopper, because the Rhino half exists to answer about
a dialog that appears before Grasshopper has loaded.

It was not a tidiness exercise. The two copies had drifted **three** times, always the same shape — a
feature both halves' protocol text advertised, implemented on the canvas side and missing on the Rhino
side:

- `dismiss` took a `key` on the canvas side and dropped it on the Rhino side. Reading the Rhino copy, an
  agent reported a bug the *serving* copy did not have and had to retract it.
- **`/pulse` reported `clickable` on the canvas side only** — found by merging the two, not by reading
  either. `docs/protocol.md` documents the field, and the Rhino half's own `GET /` tells callers to look
  at it. That half exists precisely for Rhino 8's Eto dialogs, which are the ones where `clickable` is
  false, so the field was missing exactly where it was the only thing that mattered.
- **The Rhino half still had the racing `FreePort()`** the canvas half was fixed out of: probe, release,
  bind. Two Rhinos starting together could be handed the same port, and the loser wrote a discovery file
  for a port nothing was listening on. Now both call `Loopback.Listen(out int port)`, which binds in a
  retry loop and hands the number back only once a listener is running on it. Two Rhinos starting together
  is precisely the situation the Rhino half is for, so that is where the race was most likely to be lost.

**`LinkServer.cs`, 2999 lines and one class, became a `Bridge.Verbs` namespace of seven.** It was first
split into partials named `LinkServer.Objects.cs` and so on; that stuttered, and a folder named after the
class cannot be a namespace while the class exists. So the verbs stopped being parts of the server: the
server routes, the verbs act.

| file | lines | what is in it |
|---|---|---|
| `Bridge/LinkServer.cs` | 311 | the listener and the routing table, and nothing else |
| `Bridge/Verbs/Plumbing.cs` | 208 | what every verb needs and no verb is about |
| `Bridge/Verbs/Documents.cs` | 232 | documents, the solver, history, saving, scripts, baking |
| `Bridge/Verbs/Objects.cs` | 670 | objects, wires, values, placing |
| `Bridge/Verbs/Groups.cs` | 217 | declaring a group's signature, filling it, laying groups out |
| `Bridge/Verbs/Reading.cs` | 411 | describe, wires, peek, what is installed |
| `Bridge/Verbs/View.cs` | 410 | canvas and viewport images, preview flags, the camera |
| `Bridge/Verbs/Process.cs` | 79 | Rhino as a process: saying, dismissing, escaping, friction |

What made it cheap: measuring the coupling first. Past the shared plumbing, only six members crossed a
group boundary. So `Plumbing` collects the request readers, `OnUi`, `ActiveDocument`, the autosave guard
and the four finders, and every verb file imports it with `using static` — which means **not one call site
changed**. Had they been qualified instead, the diff would have been hundreds of lines of `Plumbing.` and
the review would have been worthless.

Both moves were done by a script that carries every member block verbatim and refuses to write anything
unless the assignment accounts for the class body exactly. Then proved rather than assumed: normalising
access modifiers and sorting, the 2398 body lines before and after differ **only** in the routing table,
and stripping the seven class prefixes from that table makes it byte-identical to the original. So no verb
was silently re-pointed at another verb's handler — which the compiler could not have caught, since every
one of them takes a `JsonDocument` and returns a `string`.

**Namespaces follow the folders.** `Bridge/` holds the server, journal, friction log, command-line capture
and document watcher, with `Bridge/Verbs/` inside it; `Definition/` holds the transcriber, arrange,
catalogue, scripts, signature and review; the four files Grasshopper itself loads stay at the root. The
dependency edges came out one-way and worth having: `Definition` depends on nothing, `Bridge` depends on
`Definition`, and the plugin surface depends on `Bridge`. Nesting does the rest of the work — `Bridge.Verbs`
sees `Bridge`, which sees the root, which sees `Phenome.Apps` — so the whole move needed four `using`
lines in the root files and no qualification anywhere.

Both halves build Release clean with `TreatWarningsAsErrors`, and the canvas half was verified in a live
Rhino across every verb the reshuffle touched: `new`, `group` ×2, `place` ×2, `wire`, `set`, `param`,
`peek`, `describe`, `wires`, `select`, `zoom`, `components`, `say`, `scripts`, `rhino`, `solver` ×2,
`bake`, `arrange`, `signature` ×2, `review`, `preview`, `undo`, `redo`, `delete`, `camera`, `plugins`,
`screenshot`, `canvas-image`, `console`, `pulse`, `dismiss`. The orange working border appeared, which is
the plugin surface reaching into `Bridge` across the new namespace boundary at run time.

### The request echo, reformatted

The line Rhino's command line gets per request. Two things were wrong with it, both visible the moment you
look at a screenful rather than at one line:

```
  02:37:43 describe       ok      0ms          02:37:43  :61957 describe          0 ms
  02:37:50 set            ok    182ms          02:37:50  :61957 set             182 ms
  02:39:21 canvas-image   ok     1.4s          02:39:21  :61957 canvas-image    1.4 s
  02:41:30 bake           ok    2m05s   -->    02:41:30  :61957 bake           2m05 s
  02:41:44 delete         FAIL    31ms  ...    02:41:44  :61957 delete           31 ms  !!  would cut 3 wires
```

- **The amount and the unit are separate columns now**, so `182` and `1.4` line up on their digits and the
  slow call is found by the width of a number. Right-aligning the whole `182ms`/`1.4s` string floats the
  unit and then nothing lines up with anything.
- **Nothing says `ok` any more.** A column of fourteen identical words was the widest thing on the line and
  carried no information; what the watcher is scanning for is the line that is *not* ok. Only that one is
  marked, `!!`, in the column the eye is already travelling down — and the marker column stays aligned
  because the unit field is a fixed two characters wide.
- **The port is on every line.** It never changes within a Rhino, which is an argument against repeating it
  until you notice that the place it used to be - the banner written once at load - has scrolled off the top
  by the fifteenth request, and it is the one fact a reader needs in order to hand this session to an agent
  or to tell which of two Rhinos they are looking at. A constant column is nearly free to scan past, and it
  means any screenshot of this log carries the port with it. Written with its colon, because five bare digits
  beside a clock read as a number of unclear purpose. Padded on the right, unlike every other number here:
  lining up digits that never vary buys nothing, and a colon in a fixed column makes every line begin the
  same shape.

Evidence, stated exactly: the column layout was photographed on the real command line before the port was
added - `place 84 ms`, `place 342 ms`, `wire 392 ms`, digits aligned - which is what proves the pipeline
renders the format string faithfully in a monospace column. The port is a change to that same format string,
checked by construction. What *was* verified live afterwards is the one thing that could have broken:
`CommandLine.IsOurs` recognises the echo by its shape - two spaces, a digit, colons at 4 and 7 - and after
the change `/console` still comes back empty, so the echo is still filtered and an agent is still not shown
its own footsteps as Rhino's output.

**Superseded the same day, by looking at a screenful of the result:**

```
[00:20:12] [127.0.0.1:53911] [78 ms] new
[00:20:26] [127.0.0.1:53911] [14 ms] place  !!  'Addition' names 2 different components
```

Three bracketed facts and then the verb. Two of the notes above did not survive it, and both were wrong in
the same way — reasoned from what a column *should* want rather than from what this log holds.

The port became the whole address, because a line reading `127.0.0.1:53911` can be pasted into a request and
one reading `:53911` has to be assembled first. It now comes from the same constant the listener binds, so
the log cannot name an address nothing is listening on.

And **the duration lost its padding**, which the note above spent a paragraph defending. Aligned digits are
worth having in a column of four-digit numbers; almost every line here is two digits of milliseconds, and
padding to five made a gutter. The brackets already do that work — an eye finds `[1.4 s]` among `[78 ms]`
because the edges are drawn. The verb moved to the end for the same reason: it is the only field whose width
varies and the only one being scanned *for*, so it is the one thing that should not have columns after it.

`IsOurs` changed with it, and had to: it matches the echo by shape, and the shape now begins `[hh:mm:ss]`.
Left alone, the plugin's own lines would have started appearing in `/console` as though Rhino had said them.
Verified live after the change - `/console` still answers empty with the canvas busy.

### Graceful shutdown, which turned out to be two bugs in `Pulse`

The open question was whether closing Rhino from outside is a kill or a graceful close. It is graceful —
`CloseMainWindow()` posts `WM_CLOSE` and Rhino runs its normal shutdown — but with an unsaved definition
that shutdown stops on **Grasshopper's multi-save prompt**, and the link could not see it. Hit three times
in one session, each time needing hand-written Win32 to get out of. Two separate causes, both now fixed in
the shared `Pulse`, so both halves got the fix at once:

- **The whole diagnosis hung on `Process.MainWindowHandle`, asked for at the moment of the question.**
  Rhino destroys its frame *before* that prompt is answered, and from then on `MainWindowHandle` answers
  with the prompt itself — a visible, enabled window — so "is the main window disabled" said no and `pulse`
  reported `busy, working on something unnamed` while a dialog with a Close button held the exit. The frame
  is now recorded once, from the first idle, and never refreshed: a destroyed frame stays destroyed, and
  that is precisely the fact the shutdown case needs. With no frame there is nothing left for a window to
  be owned by, so any visible enabled window of the process is the one holding it.
- **`ButtonsOf` compared the window class for equality with `"Button"`.** A raw Win32 button's class is
  exactly that, but any framework that superclasses it registers its own name — WinForms buttons come back
  as `WindowsForms10.Button.app.0.<hash>`, and equality misses every one. Not a corner case: Grasshopper's
  own dialogs are WinForms, so the prompt reported `buttons: []`, `clickable: false` while carrying an
  ordinary `Close` button, and `dismiss` could not answer the one dialog an agent most needs to answer.
  Matched on *containing* "Button" now, case-insensitively.

Measured before and after on the same prompt:

```
before   state: busy      dialog: { present: false }                    advice: "working on something unnamed"
after    state: blocked   dialog: { title: "Grasshopper multi-save",    advice: "The dialog ... is open."
                                    buttons: ["Close"], clickable: true }
```

And then the point of it: `dismiss button:"Close"` closed Rhino, and the process exited — a graceful
shutdown driven entirely through the link, with no Win32 by hand. Worth noting what the fix did *not* need:
`RhinoApp.MainWindowHandle()` would have been the obvious source for the frame, and it was rejected because
it cannot be shown to be safe off the UI thread, and a `pulse` that can block is worse than no `pulse`.

### A bug the refactor's own verification found

**A group at the end of a definition reported no outlets, so `preview` darkened the product.**
`Signature.Ports` decided an outlet by "has a recipient outside the group" — and the terminal group has no
recipients anywhere, because it *is* the answer. Two consequences, both reproduced: `peek` on that group
answered `outlets: []` and hid the values worth reading, and the whole-document `preview` sweep, whose one
job is to leave "the outlets of the red and yellow groups" drawing, hid every object in it.

The root cause was one layer down: **the `group` verb planted ports without the mark `signature` uses**,
so a port a caller had asked for *by name* was indistinguishable from any parameter lying in the group,
and was recognised only while a wire happened to cross the boundary at it. That also meant `signature`
could plant a duplicate port in front of a declared one — the doubling its own remarks warn about.
Fixed by marking ports wherever they are planted (`Signature.MarkAsPort`) and counting a marked port fed
from inside with nothing downstream as an outlet. Verified: the terminal group now answers
`ball / Brep / 1 item / "Untrimmed Surface"`, the sweep leaves it drawing, the sphere is on screen, and
two `signature` calls in a row still add nothing.

### Behaviour fixes, all built and smoke-tested against a live Rhino

- Friction log was losing entries with two Rhinos open: one machine-wide file guarded only by an
  in-process lock, plus a read-halve-rewrite. Now a named mutex (`Local\PhenomeLinkFriction`) and
  reads through `FileShare.ReadWrite` — `File.ReadAllLines` demanded exclusivity and threw whenever
  another instance was mid-append.
- `preview` reported a delta, not a state: `drawing` was `on ? 0 : count`, so restoring always
  answered zero, which reads as failure. It cost me a long wrong diagnosis. Now `hidden`, `drawing`
  and `changed`.
- `bake` was a silent no-op — `{ok:true, baked:0}` with no reason. Now a `skipped` array
  distinguishing not-on-canvas / not-bakeable / nothing-to-bake-now / produced-nothing.
- `rhino` returned a bare `{ok:false}`. Rhino gives only a bool, so it now names the usual causes and
  points at `/console`, `/pulse` and `/escape`.
- `console` discarded the link's own lines, which made the link's faults unreadable *through the
  link*. Added `?mine=true` with its own ring.
- `place` left orphans: atomicity covered proxy resolution but not parameter names, which are checked
  in the wiring pass after every object is added. A misspelt input left seven objects standing. Now
  rolled back in reverse on any failure.
- `FromRhinoLink` built a new `HttpClient` per call — one static client now.
- `describe` reports `enabled`, `drawing` and the component's own runtime messages. That was exactly
  the information missing when a group stopped solving.
- **Stale files are swept on start, not on exit** — exit is precisely what does not always happen. Any
  `phenome-*-<pid>.port` whose process is gone goes, and autosaves older than a week. Verified: six
  port files became one, and fifty autosaves became thirteen. Every fault swallowed, because a link
  that refuses to start over somebody else's leftover file would be a far worse trade.
- `plugins` reports `shipped` correctly and now also reports `rhinoRoot`, the prefix the flag is
  decided against — a reader could not otherwise see why something was or was not marked. Finding the
  root by counting directory levels up from RhinoCommon was wrong: it sits in `System` for the .NET
  Framework load and `System\netcore` for .NET 7, so one hop landed on `System` and no plug-in path
  matched. It now walks up to whichever directory holds `Plug-ins`. Verified: `rhinoRoot` resolves to
  `C:\Program Files\Rhino 8`, `GhPython.gha` flips to shipped, and nothing under Program Files is
  left marked otherwise.
- `/canvas` reports `modified` and `path` on the document.

### New verbs

- **`camera`** — read or aim the active viewport: projection, location, target, up, 35 mm lens,
  viewport pixel size. Rhino's `Zoom` is interactive; scripting it waits for a pick that never comes
  and holds the UI thread, at which point every verb reports "busy" as though waiting would help.
- **`escape`** — post Escape to the focused window, cancelling whatever Rhino waits for. Verified on
  two different hangs:
  - a scripted interactive command: `Zoom` busy 26 s → one call → idle in 44 ms, with
    `dialog.present: false` throughout, so `dismiss` could not have answered it at all;
  - a modal save prompt that `pulse` reported as `buttons: []`, `clickable: false` — Rhino 8's own
    dialogs draw their buttons, so there is nothing to post a click to and only a key reaches them.
    One call, blocked → idle.
- **`plugins`** — Grasshopper libraries and loaded Rhino plug-ins, with version and origin. Written
  because a console message named a plug-in and attributing it took starting a second Rhino to
  reproduce the fault.
- **`sessions` with `use` / `release`** — pin a canvas session. With two Rhinos open the choice was
  made by whichever answered first, which is how an agent edits the canvas it was not looking at.

## 4. Not a problem, checked

- **Yak's `Content name doesn't match manifest: 'Phenome.Apps.RhinoLink' != 'phenome-link'` is structural.**
  Checked by opening the built package: all four files are inside, `.rhp` included, so nothing is dropped.
  Yak reads each content assembly's plugin name and compares it with the manifest's `name`. The `.gha`
  matches — `LinkLibrary.Name` is "Phenome Link", which normalises to `phenome-link` — and a package carrying
  two plugins can match at most one of them, so a warning is the only possible outcome short of splitting the
  package. Which would defeat the point: they version together and the half that reports on a stuck Rhino is
  no use sitting uninstalled.

  It earned its keep by sending somebody to look, though, because a separate wart was sitting next to it:
  Rhino's PlugInManager showed `Phenome.Apps.RhinoLink`. `PlugIn.PlugInNameFromAssembly` reads the assembly's
  `Title` attribute and falls back to the assembly name — confirmed in RhinoCommon's IL, which calls
  `GetCustomAttributes` then `get_Title` then `GetName` — and nothing set a title, so the fallback was what
  the human saw. Now `Phenome Rhino Link`, the name the plugin already gives itself when it speaks to a
  person: `OnLoad` says "Phenome Rhino Link did not start".

  Worth being exact about what was *not* shown, since the first draft of this note got it wrong: Yak's warning
  is unchanged by that, so Yak is reading the assembly or file name rather than the title. The two names come
  from different places and only one of them is what a person reads.

- **`CommandLine` stays in two copies, deliberately.** It looks like the same duplication as `Pulse`,
  and it is not: the Rhino half is the one drain of Rhino's capture buffer, because
  `CapturedCommandWindowStrings` clears as it reads and two readers halve each other's lines. The canvas
  half detects that and *borrows* over loopback instead, keeps a second ring for the link's own voice,
  and filters a request echo the Rhino half never writes. Two different jobs that share a ring and a JSON
  writer; merging them would mean a base class earning less than it cost. Written down so the next reader
  does not "fix" it.

- **`manifest.yml` belongs inside the project it does.** It looked misplaced — it describes a package
  spanning three projects while sitting in the Grasshopper one — but `tools/pack-yak.ps1` is built
  around a `$packages` list where each entry names a `Project`, and finds that package's manifest by
  that path. Its own comment says the components plugin's manifest "is written and this list is where
  it joins, when it is time". One manifest per package, keyed by project, is the design; moving it to
  `src/` would break the second package before it exists.

- `manifest.yml` appears in both `src/Phenome.Apps.GrasshopperLink/` and `dist/` and the two are
  byte-identical — but `dist/` is gitignored and untracked, and `tools/build.ps1` copies it there as
  a build step. There is one manifest in the repo. The copy in `dist/` was merely older than the
  binaries beside it.

- **`docs/protocol.md` was already right about `clickable`.** Worth saying plainly: the documentation
  described the field correctly, in the section that covers both halves, while one of the two
  implementations did not have it. The doc was not the thing that had drifted.

- **CI and both build scripts survived the file moves untouched.** They address projects and build
  outputs, never individual sources, so `Bridge/` and `Definition/` cost them nothing. The shared folder
  has no `.csproj`, so it never becomes a project of its own.
