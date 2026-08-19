# Changelog

What changed for somebody using the link, one entry per release.

**This is not a commit log, and completing it from `git log` would ruin it.** The commits are already a good
account of the work, and `git log v0.21.1..HEAD` already answers "what landed" better than a second copy
would. The difference is not access — everybody reading this can read the commits — it is that a release
note and a commit log answer different questions. Forty-one commit subjects, written for whoever is changing
the code, do not tell somebody who installed the last version which six things they are about to notice.

So: user-visible changes only, one block per release. Implementation that nobody outside sees belongs in the
commit that made it, not here.

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
