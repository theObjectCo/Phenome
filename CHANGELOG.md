# Changelog

What changed for somebody using the link, one entry per release.

**This is not a commit log, and completing it from `git log` would ruin it.** The commits are already a good
account of the work, and `git log v0.23.0..HEAD` already answers "what landed" better than a second copy
would. The difference is not access — everybody reading this can read the commits — it is that a release
note and a commit log answer different questions. Forty-one commit subjects, written for whoever is changing
the code, do not tell somebody who installed the last version which six things they are about to notice.

So: user-visible changes only, one block per release. Implementation that nobody outside sees belongs in the
commit that made it, not here.

## 0.32.0

### Security

- **A web page you visited could drive your canvas and run code in Rhino. Update, and the sooner the
  better.** Every version up to and including 0.31.0 is affected, on every machine where Rhino and
  Grasshopper were open.

  The link binds `127.0.0.1`, and that was read as "only this machine, therefore only things the user
  already trusts". The first half is true and the conclusion does not follow. **A browser is a program on
  that machine**, and a page can ask it to `fetch('http://127.0.0.1:53812/place', {mode:'no-cors', ...})`.
  The request leaves your own computer, arrives on loopback, and is indistinguishable from a local client.
  Nobody needs to reach your port from outside; they need you to open a tab.

  What that reached was the whole protocol, `script_write` included, which compiles and runs C# inside
  Rhino. So: arbitrary code execution, on a machine that merely had Rhino open, triggered by visiting a
  page.

  It was made materially worse by a header this release removes. The server answered every request with
  `Access-Control-Allow-Origin: *`, so a page could **read** the replies. Without it a browser gets an
  opaque response and learns nothing, and finding which of sixteen thousand ports is a canvas is a
  different problem from knocking until one says `grasshopper-link`.

- **The link now refuses anything a browser sends.** A request carrying `Origin`, `Referer` or any
  `Sec-Fetch-*` header is answered 403 and a sentence saying why. Browsers attach at least one of those to
  every request they make and cannot be talked out of it; the MCP server, the extension, a script and curl
  attach none. `Host` is checked too, which answers the trick of pointing your own domain at 127.0.0.1 so
  the browser believes it is same-origin and sends no `Origin` at all.

- **Install the `.gha`, the `.rhp` and the `.vsix` from this release together.** A `POST` must now carry
  `Content-Type: application/json`, and an older extension does not send it, so a plugin updated ahead of
  its extension will refuse that extension. The refusal says so in as many words rather than leaving you
  with a header name.

  This is the check worth having, and it is different in kind from the ones above. Those are negative — a
  request is refused for carrying something — and they hold only while browsers keep sending headers they
  send today. This one is positive: the request has to prove something, and a page **cannot**. A `no-cors`
  request may use `text/plain`, `x-www-form-urlencoded` or `multipart/form-data` and nothing else; asking
  for JSON makes it non-simple, the browser sends a preflight first, and a server that does not answer
  preflights with permission ends it there.

  It does not replace the `Host` check, which answers a different trick: point your own domain at
  127.0.0.1 and the browser considers the request same-origin, so there is no preflight and any content
  type is allowed. Three checks because there are three ways in.

### Added

- **The link can be withdrawn remotely when a version turns out to be unsafe, and says so.** It reads one
  static file in this repository, in the background, and compares on your machine. If the version you are
  running has been withdrawn, the canvas link stops answering its verbs and tells you which version to move
  to; Rhino and Grasshopper are untouched.

  Four things about it, because a plugin that can disable itself deserves plain description rather than a
  line in a feature list. **It sends nothing** — the whole notice is fetched and compared locally, so there
  is no telemetry and no way to learn who runs what. **It fails open** — no network, blocked domain, bad
  file, no effect. **It withdraws the link and nothing else** — taking away a CAD application because a
  bridge has a bug would be wildly out of proportion. And **it can be overridden**, with
  `PHENOME_IGNORE_ADVISORY=1`, which says so at every startup: a mistake on our side would otherwise stop
  your work with no recourse, and the machine is yours.

  It exists because this release is the kind of thing it is for. Deleting a release uninstalls nothing, and
  the people most at risk are the ones not reading this file.

## 0.31.0

### Added

- **`dialog` answers the dialog Rhino is waiting on, and assumes nothing.** `button` presses one by name,
  `key` types into a dialog that draws its own buttons and cannot be clicked, `close` declines. Send one of
  the three: with **no answer given it refuses and lists what the dialog offers**, rather than deciding for
  you.

  That last part is the point. `dismiss` treated "nothing said" as close, close means decline, and a decline
  by omission cannot be told from a decline by decision — so the safe-looking call was the one that said no.
  A field report has an agent closing a load confirmation twice, declining the very load it was trying to
  confirm, before reading `pulse` and working out that OK was the yes. The old name did the rest of the
  damage: agreeing to something read as *dismissing* it with the OK button.

  No verb guesses which button means yes, and none will: on a save prompt the affirmative is whichever of
  Save and Don't Save you meant, and guessing there writes or discards somebody's file. `pulse` lists the
  buttons; you name one.

### Deprecated

- **`dismiss` is superseded by `dialog` and still works exactly as before**, including "nothing means
  close". Its behaviour was left alone rather than corrected: a caller that sends nothing in order to
  decline would otherwise stop declining without being told, and would find the dialog still there, asking
  again. It will be removed a release or two from now.

## 0.30.0

### Changed

- **The MCP tools are now `mcp__phenome__*`, not `mcp__grasshopper__*`. Run *Pair with VS Code* again.**
  The pairing rewrites the registration in all five host files and the permission rule, and removes the old
  name as it goes — leaving both would register two servers, spawn two copies of the script, and offer every
  verb twice. Until you re-pair, a host that was set up before this version will find no tools at all.

  The old name described the half that came first. It stopped being true some releases ago: commands, the
  document, the command line, the dialogs and now the plug-ins are all answered by the Rhino half, in a
  Rhino that never opened a canvas. A prefix saying *grasshopper* on those verbs sent agents looking for a
  canvas they did not need — including one that started Grasshopper in order to install a Rhino plug-in.

- **`screenshot` and `camera` no longer need Grasshopper running.** A viewport is Rhino's, and answering
  about it from the canvas half meant starting Grasshopper to photograph a Rhino window. Neither verb ever
  touched Grasshopper — they were written where the server happened to be at the time. Both now answer from
  the Rhino half, and a pairing where only the canvas side is current still works, because the client falls
  back there.

### Added

- **`plugins` answers "why is my plug-in not loading".** It now reports every plug-in Rhino holds a record
  of, **including the ones that are not loaded**, each with the path Rhino believes, whether Rhino thinks
  the assembly is managed, whether it is load protected, and the registry key. A record present with
  `loaded:false` rules out the registry in one call, which is otherwise a morning of `reg query` against a
  plug-in that works. It also carries the runtime Rhino is hosting, because Rhino 8 hosts two CLRs — the
  executable is .NET Framework and there is a .NET Core half beside it — and a plug-in has to match
  whichever one is running. Answered by the Rhino half, so it works with no Grasshopper open.

- **`rhino_load`** loads a plug-in by id or by path to an `.rhp`, quietly and again even after a failed
  attempt. Both matter: every plug-in somebody installed is load protected, so the confirmation dialog is
  the normal case rather than the odd one, and Rhino remembers a failure and will not retry — which is why
  a rebuild-and-load loop appears to do nothing the second time round. Turning load-protection asking off
  globally is not the same thing and is a trap: Rhino then silently does not load a protected plug-in at
  all.

- **`restart`** ends this agent's Rhino and brings a fresh one up, waiting until the link answers. This is
  the unit of iteration for plug-in work, because a .NET assembly cannot be unloaded from Rhino — there is
  `LoadPlugIn` and no `UnloadPlugIn` — so a rebuilt file reaches a running Rhino only through a new
  process. It refuses while either half holds unsaved work, since the process is ended outright and there
  is no save prompt on the way out; `discard:true` says you meant it. Only the process this agent is
  working in is ended, so a second Rhino somebody else is using survives.

- **`launch` says to use `grasshopper:false` for plug-in work.** Building, installing and loading a Rhino
  plug-in needs no canvas, and starting Grasshopper for it costs a slower launch and one more thing that
  can fail to load.

## 0.24.2

### Added

- **Every open document is now listable, switchable and closable.** `documents` answers what Grasshopper is
  holding open — id, name, path, whether it has unsaved edits, how many objects — and says which one the
  canvas is showing; passing `use` points the canvas at another. This matters more than it sounds, because
  `new` and `open` have always left the previous document **open**: it keeps its unsaved edits and becomes
  unreachable, since every verb speaks to whichever document is on the canvas. A long session had been
  quietly accumulating them with no way to see it, let alone get back.

  Closing is two verbs rather than one with a flag: `close` discards whatever is unsaved, `saveandclose`
  writes it first and refuses a document that has never been saved rather than inventing a location for it.
  Naming the destructive one is the point — a forgotten flag looks exactly like a considered one. Neither
  puts up a save prompt: a modal dialog holds the thread every verb needs, so the choice is the verb you
  picked, and `close` reports `discardedUnsavedChanges` when something was in fact thrown away.

### Fixed

- **Wires stop showing a selection that nothing could clear.** After the link placed a component, clicking
  near one of its sockets lit every wire into it as though the component were selected, while the component
  itself was not — and no amount of clicking, deselecting or Escape put it out. Standing an object up called
  for new attributes on a component that already had them, which left its parameters pointing at an object
  the document could no longer reach; the selection landed there, where nothing could reach it either.
  Existing files were never damaged and need nothing done to them — reopening one has always been enough.

- **A group reports the signature you declared, before anything is wired to it.** A group whose inlets and
  outlets had just been declared by name came back with no ports at all, and `peek` then advised calling
  `signature` — the very thing that had just been done. An inlet holding a constant typed into its socket
  disappeared the same way. The side a port stands on is now recorded when it is planted instead of being
  inferred from wires that are not there yet.

- **The first verb after `launch` no longer fails with "There is no document".** A freshly opened Grasshopper
  holds no document at all — it shows its start screen and makes one when somebody drags a component onto
  the canvas — so the opening call of a session was refused for a reason nobody could see, and the remedy
  was a verb you had to know to ask for. `add`, `place` and `group` now make a document when there is none,
  which is what Grasshopper does itself. Reading still answers honestly that there is nothing there.

## 0.24.1

### Fixed

- **Pair with VS Code registers the server for every host, not only for Claude Code.** It wrote `.mcp.json`
  and nothing else, which is where *Claude Code* looks — Kilo Code, Roo Code, Cline, Cursor and VS Code's own
  MCP support each read a different file and none of the others. So on any of those the pairing planted notes
  written almost entirely in terms of the `grasshopper` tools, and registered those tools nowhere the host
  would find them: the agent saw no tools at all and had no way to know why. The registration now goes into
  `.mcp.json`, `.kilocode/mcp.json`, `.roo/mcp.json`, `.cursor/mcp.json` and `.vscode/mcp.json` — merged, so
  whatever else those files carry stays — and the last of them is keyed `servers` rather than `mcpServers`,
  which is VS Code's spelling and is silently ignored if you use the other one.

  **If you paired before this version, run *Pair with VS Code* again**; it is idempotent and will add the
  files that were missing.

- **The notes no longer send an agent to restart a session that was never the problem.** They said a missing
  tool means the session predates the server, so restart it. That is true of *one* missing tool and useless
  when there are no `grasshopper` tools at all — the host simply has no server wired up. The two cases are
  now told apart, and the second one points at the HTTP protocol instead of at a pointless restart.

- **The HTTP fallback says how to start a session.** It could not be worked out from `GET /`, and there is no
  verb for it — the server lives inside Grasshopper, so nothing answers until Grasshopper runs. Both ways it
  goes wrong are now written down: Rhino started on its own writes a `phenome-rhino-*.port` and never a
  `phenome-link-*.port`, and the `/runscript` argument has to be quoted as one whole token or some shells
  double the inner quotes, Rhino runs no script, and the result is indistinguishable from Rhino being slow.

## 0.24.0

### Changed

- **A refused `place` tells you everything that is wrong with the recipe, not the first thing.** The recipe is
  still all-or-nothing — one bad entry and nothing is placed, the canvas untouched — but the refusal now names
  **every** entry it could not resolve, keyed by your own local id. Reported from the field: a thirteen-object
  recipe with six name collisions in it came back six times, once per collision, because each refusal
  mentioned only the first. One pass of edits now fixes the batch.

- **An ambiguous component name comes back as something to paste.** The candidates were already listed, as
  prose with the guid at the end of a sentence; they now arrive as `{"name":"Merge","guid":"3cadddef-…"}` with
  the category *and* each candidate's own description. The description is not decoration: both `Merge`
  components live in `Sets › Tree`, so the category alone cannot tell them apart, and a reader picking by the
  label had no way to know which was which.

- **`preview` works on a single object, and on a list.** It took a group id or nothing at all, which missed
  the case that prompted it: one intermediate component flooding the viewport while the rest of its group has
  to keep drawing. A facade of 960 panels interpolated through 24 points each put **23,040 preview markers**
  over the building, and neither the human nor any screenshot could see the product underneath. `id` now takes
  a group or an object, `ids` takes a list of either, and `on:true` gives any of them back. Every id is checked
  before a single flag moves, so a bad one refuses the batch and says which.

- **`set` with `param: "preview"` says which verb does that instead.** It refused accurately — a Construct
  Point has no such parameter — and the author reading it concluded, having also checked `param` and `canvas`,
  that nothing could turn a preview off. The `preview` verb had been doing it for releases. A refusal that
  leaves the reader worse informed than the server is a fault of its own.

### Added

- **The pairing notes carry each component's `ComponentGuid`.** Same table, one more column, and it changes
  how an agent should place anything: a guid is what a `.gh` file stores in order to find a component again,
  so it is the one identity that cannot drift, while a display name can be renamed by a plugin author, a
  nickname is editable per instance, and ribbon categories get reorganised. Naming by guid also skips the
  ambiguity refusal entirely — which is six refusals in the definition that prompted this.

## 0.23.0

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

- **`arrange` is idempotent.** Running it twice ran the definition twice across the canvas: the layout anchors
  on the top-left of where the objects were, but inside a group the first object sits inset by the frame's
  padding and its label, so every run added that inset again — 26 by 52 pixels at a time, for ever. It only
  showed when the top-left-most object was in a group, which is why it looked fine when tested on loose
  objects. A settled document now answers `moved: 0` and the coordinates do not change.

- **New objects land in free space instead of on the origin.** `add` left an object's position unset, which
  put it at 0,0 — on top of whatever was already there, and on top of the next object added the same way.

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
