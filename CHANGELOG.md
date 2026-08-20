# Changelog

What changed for somebody using the link, one entry per release.

**This is not a commit log, and completing it from `git log` would ruin it.** The commits are already a good
account of the work, and `git log v0.21.1..HEAD` already answers "what landed" better than a second copy
would. The difference is not access — everybody reading this can read the commits — it is that a release
note and a commit log answer different questions. Forty-one commit subjects, written for whoever is changing
the code, do not tell somebody who installed the last version which six things they are about to notice.

So: user-visible changes only, one block per release. Implementation that nobody outside sees belongs in the
commit that made it, not here.

## Unreleased

### Fixed

- **A verb reported as failed no longer turns out to have run.** `wire` and `set` batches could answer
  "Rhino is busy: the UI thread is working" and be applied anyway: work is queued onto Rhino's thread, and
  giving up waiting for it did not unqueue it. An agent that retried on that answer applied it twice. The wait
  is now a handshake — work that has not started can be abandoned, work that has started is waited for — so
  the answer is one of three true things: it ran, it never started, or it started and is still going. Never
  "it failed" about something that happened.

- **A client that disappears no longer looks like a broken verb.** When an answer could not be delivered
  because the caller had gone, the write failure was treated as the verb failing: the friction log gained an
  entry for a verb that had run, the command line echoed a failure that had not happened, and the error handler
  tried to answer a second time on a closed connection — which is where "this operation cannot be performed
  after the response has been submitted" came from. In one two-agent session, **947 of 1132 friction entries**
  were this and nothing else. Delivery failures are now counted, not logged as refusals.

- **One agent's long verb no longer locks the other out.** Requests were answered on the accept loop itself,
  so a two-minute bake meant the next request was not queued behind it — it was not accepted at all, and the
  second client's own timeout fired. Requests are now accepted while one is being answered. Document work is
  as serialised as it always was; what runs in parallel is the part that never needed Rhino.

- **A note's text is no longer silently dropped.** `place` read `text` for a `Panel` and ignored it for a
  `Scribble`, answering `ok` while the canvas said "Doubleclick Me!". Reported by an agent who only found out
  because a human sent it a screenshot. `Scribble` now takes `text` on create and can be reworded with `set`,
  which is the repair path when the first wording was wrong; empty or whitespace text is refused on both
  rather than becoming a placeholder.

### Added

- **Annotations can be read back.** `describe` on a note answers its `text`, where it sits (`at`), the
  rectangle it covers (`box`) and the group it belongs to; `canvas` carries the same for every note. Until now
  a note had no readable position at all, which made every fix to it unverifiable from an agent's side — it
  could write one and had to believe. Placement is the half that went wrong, and a box can be checked against
  another box without anybody looking at a screen.

- **`arrange` places notes, as captions.** A note's group is what the note is about, so that is all the
  instruction the layout needs: a note in a group becomes that group's caption and is put above the group's
  other members, a note in no group is about the whole definition and is put above everything as a title.
  Before this, `arrange` moved every component and left the notes where they were, which is how a scribble
  ended up lying across the sliders it was written to explain. Nothing new to pass — an author already says
  which kind of note it is by giving `place` a `group` or not.

- **Notes appear in the mermaid diagram.** `canvas` with `as:'mermaid'` renders each one as `[/"the text"/]`,
  inside its group's subgraph or loose at the top level, so reading a definition back gives you its comments
  and not only its wiring.

- **`group` plants declared ports on a group that already exists.** Calling it again with `inlets` or
  `outlets` used to accept them and silently do nothing, so a group could not be given a signature after the
  fact. Missing ports are now planted, matched by nickname, and calling it twice adds nothing the second time.

### Fixed — layout

- **`arrange` is idempotent.** Running it twice ran the definition twice across the canvas: the layout anchors
  on the top-left of where the objects were, but inside a group the first object sits inset by the frame's
  padding and its label, so every run added that inset again — 26 by 52 pixels at a time, for ever. It only
  showed when the top-left-most object was in a group, which is why it looked fine when tested on loose
  objects. A settled document now answers `moved: 0` and the coordinates do not change.

- **New objects land in free space instead of on the origin.** `add` left an object's position unset, which
  put it at 0,0 — on top of whatever was already there, and on top of the next object added the same way.

### Changed

- **The request echo in Rhino's command line is bracketed, and carries the whole address.**

  ```
  [00:20:12] [127.0.0.1:53911] [78 ms] new
  [00:20:26] [127.0.0.1:53911] [14 ms] place  !!  'Addition' names 2 different components
  ```

  Three bracketed facts and then the verb: when, from where, how long, what. The verb goes last because it is
  the only part whose width varies and the only part being scanned *for* — anything variable in the middle
  pushes the columns after it out of line. The address is whole rather than just the port, so a line can be
  pasted into a request instead of assembled first. The duration is not padded: aligned digits are worth
  having in a column of four-digit numbers and read as a gutter in one where almost every line is two digits
  of milliseconds, and the brackets already do that work.

- **A release carries the plugin files, not a Yak package.** There is no public package server to publish to,
  so the package was being built and attached for nobody: installing it still meant downloading a file and
  running `yak install` by hand, which is no easier than dropping the `.gha` and `.rhp` into the Grasshopper
  components folder. The build no longer makes one. `tools/pack-yak.ps1` is still there and still works if you
  want a package for your own distribution.

## 0.22.0

### Changed

- **An agent's edit marks the document modified, like anybody else's.** So closing Rhino offers to save, and
  the Grasshopper title carries the usual asterisk while there is work outstanding. Until now the link
  changed a document and left the flag alone, which meant Rhino closed it without asking and an agent's work
  could disappear with no prompt at all.

  Reading never marks it, and neither does selecting or zooming. `arrange`, `signature` and `preview` mark
  only when they actually changed something — all three are finishing moves people run more than once, and a
  save prompt for having run one twice teaches everybody to dismiss the prompt unread.

- **The request echo in Rhino's command line reads as columns, and carries the port.** The duration's number
  and its unit are separate columns, so a slow call is found by the width of a number rather than by reading;
  nothing says `ok` fourteen times, and only the one failure is marked. The port is on every line because the
  banner that used to carry it has scrolled off the top by the fifteenth request — and now any screenshot of
  the log says which session it came from.

### Added

- **`escape`** — cancels whatever Rhino is waiting for. The case `dismiss` cannot answer: a command waiting on
  a pick is not a dialog, so nothing is disabled and there is no window to click, yet the thread is held all
  the same and every other verb reports *busy* as though waiting would help.
- **`camera`** — read or aim the active viewport. Rhino's own `Zoom` is interactive, and scripting it waits
  for a pick that never comes, which hangs the UI thread.
- **`plugins`** — what is loaded, with versions and where each came from. For when a console message names a
  plug-in and attributing it would otherwise take starting a second Rhino.
- **`sessions` with `use` and `release`** — pin one canvas. With two Rhinos open the choice used to be made by
  whichever answered first, which is how an agent edits the canvas nobody was looking at.
- **`console?mine=true`** — the link's own lines, which `console` leaves out so an agent does not read its
  requests back as Rhino's answers. Wanted precisely when the suspicion is that the bridge, not Rhino, is at
  fault.

### Fixed

- **Saving through the link left the Grasshopper window saying "unnamed".** The title was cached and rebuilt
  from five places, none of which a save through the link went through. It now says the file's name, and the
  save clears the modified flag rather than leaving Rhino offering to save what you just saved.
- **A group at the end of a definition reported no outlets**, because an outlet was decided by "has a
  recipient outside the group" and a terminal group has none — it is the answer. So `peek` hid the values
  worth reading, and the whole-document `preview` sweep darkened the very geometry it exists to leave
  drawing.
- **Data mapping was stored and ignored.** `param` set flatten, graft, simplify or reverse and the tree came
  through unchanged — or, on an output, stopped coming through at all. Both sides now take effect on the next
  read.
- **`arrange` is idempotent.** It reported every object as moved even on a settled layout, and pushed an undo
  step per object that undid nothing. Running it twice is normal; the second run now reports nothing and
  records nothing.
- **Rhino can be closed through the link.** Closing a document with unsaved changes stops on Grasshopper's
  multi-save prompt, and by then Rhino has destroyed its own frame — so `pulse` reported *busy, working on
  something unnamed* while a perfectly clickable dialog held the exit. It is now named with its buttons
  listed, and `dismiss` ends the process cleanly. Separately, every WinForms button was invisible to the
  button scan, and Grasshopper's dialogs are WinForms.
- **`/pulse` reports `clickable` from the Rhino plugin too.** It only ever came from the canvas plugin, while
  both halves' protocol text told callers to look at it — and the Rhino half exists precisely for the dialogs
  where it is false.
- **`dismiss` honours `key` in the Rhino plugin too.** It was read and thrown away there.
- **Two Rhinos at once.** The friction log stopped losing entries, and two starting together stopped being
  able to claim the same port and leave the loser advertising one that nothing listens on.
- **`bake` says why it did nothing** instead of answering `baked: 0` with no reason. **`describe` reports** a
  component's own runtime messages, `enabled` and `drawing`. **`place` rolls back** everything it added when a
  later step fails, rather than leaving orphans on the canvas.
- **Leftover files are swept at startup** — port files whose Rhino is gone, and autosaves older than a week.
  At startup rather than on exit, because exit is precisely the moment that does not always happen.

## Before 0.22.0

Not reconstructed here, because notes written after the fact are worth less than the record that already
exists. `git tag` lists the releases and each is a commit that says what it was for — `v0.21.1` and `v0.21.0`
were the Rhino plugin actually reaching the package, and `v0.20.0` was that plugin arriving at all, so Rhino
could say what it was doing and be answered while the canvas could not.
