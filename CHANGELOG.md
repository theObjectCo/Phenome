# Changelog

Each release has one entry here, describing what changed for somebody using the link.

**This is not a commit log, and completing it from `git log` would ruin it.** The commits are already a good
account of the work, and `git log v0.23.0..HEAD` already answers "what landed" better than a second copy
would. Everybody reading this file can read the commits as well; a release note answers a different question
from a commit log. Forty-one commit subjects, written for whoever is changing the code, do not tell somebody
who installed the last version which six things they are about to notice.

Each release block lists only the changes a user can see. Implementation that nobody outside sees belongs in
the commit that made it.

## 0.34.0

### Added

- **`measure` answers the sizes of the geometry on a parameter.** Per item it gives a curve's length, the area
  of a closed planar curve, and the area and closed volume of a brep or mesh, with totals and a bounding box.
  With `against` it compares two sets pair by pair and answers the area two closed planar curves share, the
  volume two solids share, and the nearest distance between curves or points. The same parameter given twice
  is compared with itself. One call compares at most 2,500 pairs.

- **`arrange` stacks sources in the order of the sockets they feed.** The source of a component's first input
  stands above the source of its second, and the groups feeding a group stand in the order of that group's
  inlets. A group with nothing upstream is placed in the column just left of its nearest reader.

- **A parameter stores a list.** An array in `value` on `set` and `place` stores one item per element. Two or
  three numbers, `[x,y,z]`, make a point, and a Point parameter takes the corners of a polyline directly as
  a list of such arrays. A value the parameter cannot read causes the whole entry to be refused and leaves the socket
  unchanged.

- **A panel can be a list source.** An array of strings as `text` on `place` or `value` on `set` turns off
  Grasshopper's Multiline Data, and the panel sends one item per line. A string still leaves as one item, as
  it does from every new panel. `canvas` reports `multiline` per panel.

- **`set` renames a parameter and sizes a panel.** `nickname` renames a standalone parameter, such as a
  group's inlet or outlet, and is refused on a component. `width` and `height` size a Panel, from 20 up.
  With either, `value` may be left out.

- **`launch` and `restart` take `packageDirs`.** The folders are passed to Rhino as `RHINO_PACKAGE_DIRS`. A
  plug-in that loads from its build folder can now be started through the tool, and `restart` reuses the
  folders from the last launch.

- **The first canvas edit from an author over plain HTTP carries a `door` field.** It names the MCP tools and
  states that a script suits loops of `set`, `peek` and `measure`. The MCP server and the VS Code extension
  send `X-Phenome-Client` and never receive it. Each author receives it once per Rhino.

- **Rhino 8 for Mac is prepared and untested.** The package has always been offered on a Mac, but nothing in
  it had been made to run there. `launch` and `restart` start Rhino for Mac with `-runscript=_Grasshopper`,
  and `PHENOME_RHINO` points them at a Rhino installed elsewhere on either system. Reading and answering
  dialogs is Win32 code, which is disabled on a Mac: `pulse` reports an open dialog as busy and adds
  `dialogsReadable: false`, and `dialog`, `dismiss` and `escape` return an error. The VS Code extension looks
  for the `claude` binary by its Mac name and quotes its path for zsh. None of this has been tested on a Mac;
  the README asks for reports from users who run it.

### Changed

- **`launch` waits for the Rhino it started.** Startup can take longer than 90 seconds when Rhino shows a
  window before its main one, and `launch` previously gave up after 90 seconds with an error naming a missing
  plug-in. Now, after 90 seconds without a port the answer begins `NOT UP YET`, gives the window title where
  Windows can read it, and asks the user to look. Calling `launch` again waits for the same process. A Rhino
  that exits during startup is reported as having ended.

- **The pairing notes tell a dropped server from a missing one.** They previously said that no `phenome` tool
  means no server and to move to plain HTTP. They now name three cases: deferred tools that one `ToolSearch`
  loads, a dropped server that needs reconnecting, and a host with no server. They also state that a loop of
  `set`, `peek` and `measure` may run as a script while building the definition goes through the tools.
  *Teach Agents* writes the new notes into a workspace.

- **`preview` with no id quiets objects in no group as well.** The sweep previously refused on a canvas with
  no groups at all. Objects outside every group are now quieted with the rest, and the answer carries an
  `ungrouped` block with the counts.

- **`preview` skips an object that draws nothing and quiets the rest of the list.** Such an object previously
  refused the whole batch; it is now listed under `skipped`. An id that is not on the canvas still refuses the
  batch.

### Fixed

- **The headless Rhino server no longer sends `Access-Control-Allow-Origin: *`.** It already refused requests
  from web pages, and with that header a page could still read the refusal and find which port the server
  listens on. The canvas link has never sent the header.

- **`arrange` settles on the second run.** Previously, sixteen unconnected groups came out in reverse order on
  every run, about 160 objects moved each time, and two groups feeding each other swapped columns on every
  run. Both orders came from document order, which `arrange` reverses when it sends frames to the back. Columns
  now start from where the blocks stand, and a cycle is walked in guid order. A real definition of 93 objects
  also moved 31 of them by a pixel on its second run. Pivots are planned in whole pixels and written once, and
  an object within a pixel of its place stays put. Three runs on that definition moved 81, 0 and 0 objects.

- **`arrange` keeps captions clear of components and of neighbouring groups.** A group's block reserves the
  height of its captions and the width of the widest one, and the captions stack into that band. A mother
  group's caption used to be placed against its child groups' frames and landed on a component. A caption
  wider than its group pushed the frame into the next group. An unwired panel is now laid out as a caption
  only.

- **Edits made while the Grasshopper editor is hidden solve.** Hiding the editor disables the document it was
  showing, and `place`, `wire` and `set` on it answered ok and computed nothing. Every verb enables the
  document again before it solves. `canvas` reports the document's `enabled` flag.

- **`signature` gives a producing group its outlet whichever group it signs first.** When the consumer was
  signed first, its inlet took the wire directly from the producer's member, and the check for the producer
  counted that inlet as a port already in place. One report had five groups left without outlets, however
  often `signature` ran.

- **The `preview` sweep keeps the product of a red or yellow group with no outlet drawing.** Such a group
  used to go wholly dark, its final component included. Without an outlet, the members that nothing else in
  the group reads keep drawing, and the answer reports this under `kept`.

- **`review` no longer blocks a component that pairs two equal lists.** A Circle CNR fed 1500 centres and
  1500 radii makes 1500 circles, and review called it blocking twice, once per input. The check counts runs
  the way Grasshopper pairs branches and items. "multiplies" is blocking only when a component runs more than
  100 times in one branch and more often in total than its largest input has items. A smaller product of that
  kind, such as a grafted input making a 20 by 20 grid, is the polish finding "crosses". Two lists of
  different lengths in one branch are the polish finding "uneven lists", and plain broadcasting stays polish
  with one finding per component.

- **`review` no longer reports an outlet feeding a sibling group's inlet as chained ports.** Inside a mother
  group both ends were judged against the mother, which contains both.

- **`set` with `param` given as a number reaches the socket.** An index sent as `0` and not `"0"` was read as
  no `param`, and the value went to the component, which "holds no value to set".

- **A colour given as four numbers keeps its alpha.** `set` on a Colour Swatch read "120,215,205,190" as an
  opaque colour and dropped the fourth number without a warning. Text now goes through Grasshopper's own
  parser, which reads four numbers as r,g,b,a with the alpha last, and `[r,g,b,a]` works as an array. Other
  counts are refused with the accepted spellings. A word that is not a known colour name is refused too,
  where Grasshopper's parser on its own turned "not a colour" into transparent black.

- **The first camera call after `launch` reaches the new Rhino.** The MCP server kept the Rhino-half port of
  the previous session. After a crash or a closed window the first `camera`, `pulse` or `console` call failed
  with "stopped answering". After `launch fresh:true` with the old Rhino still open, those calls went to the
  old Rhino.

- **`canvas_image` states why it cannot draw a minimised canvas.** A minimised Grasshopper window shrinks the
  canvas to 0 x 0 pixels, and the answer was GDI+'s "Parameter is not valid." It now gives the size and asks
  for the window to be restored.

- **The 403 for a missing JSON content type names the likely cause.** A client older than 0.32.0 sends no
  content type or Node's default `text/plain`. The usual sender is a `.phenome/gh-mcp.js` written by the
  previous extension, typically when Teach Agents ran before the new extension was installed. The refusal now
  says to update the extension and run Teach Agents again.

- The `screenshot` description now states that geometry a plug-in draws with its own display code can be
  missing from a capture while the screen shows it.

## 0.33.0

### Added

- **A canvas picker, for a machine with more than one Rhino open.** Previously the extension used whichever
  session it found first, and on a machine running three that choice could be neither observed nor
  controlled. Clicking the status bar now lists every Grasshopper that answers, newest first, with its
  document name, its port and the Rhino process it belongs to, and selecting one **pins** it. The status bar
  shows a pin while a choice is held. The pin lasts until that session stops answering, and neither polling
  nor a restart can switch to a different canvas without notice. `Phenome Link: Switch Canvas` does the same
  from the Command Palette.

- **Two preview panels: the canvas and the Rhino viewport.** `Phenome Link: Show the Canvas` and
  `Phenome Link: Show the Rhino Viewport` open a panel each, with a refresh button and nothing else. Both
  query the pinned session and show the new canvas after a switch.

  Both are mainly for working away from the machine. Over a VS Code tunnel the editor is the only window
  available, and until now the canvas could only be seen by asking an agent to describe it. See
  [docs/from-anywhere.md](docs/from-anywhere.md).

## 0.32.0

### Security

- **A visited web page could drive the canvas and run code in Rhino. Update, and the sooner the
  better.** Every version up to and including 0.31.0 is affected, on every machine where Rhino and
  Grasshopper were open.

  The link binds `127.0.0.1`, and that was read as "only this machine, therefore only things the user already
  trusts". The first half is true and the conclusion does not follow: **a browser is a program on that
  machine**, and a page can ask it to `fetch('http://127.0.0.1:53812/place', {mode:'no-cors', ...})`. The
  request starts on the user's own computer, arrives on loopback, and is indistinguishable from a local
  client. No external access to the port is needed; opening a tab is enough.

  What that reached was the whole protocol, `script_write` included, which compiles and runs C# inside Rhino.
  That is arbitrary code execution on a machine that merely had Rhino open, triggered by visiting a page.

  A header, which this release removes, increased the exposure. The server answered every request with
  `Access-Control-Allow-Origin: *`, and a page could **read** the replies. Without the header a browser gets
  an opaque response and learns nothing. Identifying which of sixteen thousand ports is a canvas is then a
  different problem from sending requests until one answers `grasshopper-link`.

- **The link now refuses anything a browser sends.** A request carrying `Origin`, `Referer`, `Sec-Fetch-Site`
  or `Sec-Fetch-Dest` is answered 403 with a sentence stating why. Browsers attach at least one of those to
  every request, and a page cannot prevent it. The MCP server, the extension, a script and curl attach none
  of the four. Only those two of the `Sec-Fetch-*` family are checked, a narrowing that was measured: Node's
  own `fetch` sends `Sec-Fetch-Mode`, and refusing the whole family would have refused the link's own
  clients. `Host` is checked too. That check covers a domain pointed at 127.0.0.1, for which the browser
  treats the request as same-origin and sends no `Origin` at all.

- **Install the `.gha`, the `.rhp` and the `.vsix` from this release together.** A `POST` must now carry
  `Content-Type: application/json`, and an older extension does not send it. A plug-in updated ahead of its
  extension will refuse that extension. The refusal states this explicitly and does not return only a header
  name.

  This check is different in kind from the two above. Those are negative checks: a request is refused for
  carrying something, and they hold only while browsers keep sending the headers they send today. This one
  is positive: the request has to prove something, and a page **cannot**. A `no-cors` request may use
  `text/plain`, `x-www-form-urlencoded` or `multipart/form-data` and nothing else. Requesting JSON makes it
  non-simple, the browser sends a preflight first, and a server that does not answer preflights with
  permission ends the request there.

  It does not replace the `Host` check, which covers a different case. A domain pointed at 127.0.0.1 makes
  the browser treat the request as same-origin, with no preflight and any content type allowed. Three checks
  are needed because there are three separate paths.

### Added

- **The link can be withdrawn remotely when a version proves unsafe, and says so.** It reads one static file
  in this repository, in the background, and compares versions on the user's machine. If the running version
  has been withdrawn, the canvas link stops answering its verbs and states which version to move to; Rhino
  and Grasshopper are untouched.

  A plug-in that can disable itself should be described exactly, and this one behaves as follows. **It sends
  nothing**: the notice is fetched and compared locally, with no telemetry and no way to learn who runs what.
  **It fails open**: no network, a blocked domain or a malformed file has no effect. **It withdraws the link
  and nothing else**, because disabling a CAD application over a bug in a bridge would be disproportionate.
  **It can be overridden** with `PHENOME_IGNORE_ADVISORY=1`, which is reported at every startup. Without an
  override, a mistake by the maintainers would stop the user's work with no recourse.

  This release is the case it exists for. Deleting a release uninstalls nothing, and the people most at risk
  are the ones not reading this file.

## 0.31.0

### Added

- **`dialog` answers the dialog Rhino is waiting on, and assumes nothing.** `button` presses one by name,
  `key` types into a dialog that draws its own buttons and cannot be clicked, and `close` declines. Send one
  of the three; with **no answer given it refuses and lists what the dialog offers**, and it chooses nothing
  on the caller's behalf.

  `dismiss` treated "nothing said" as close, and close means decline. A decline by omission cannot be
  distinguished from a decline by decision, and the call that looked safe was the one that refused. In one
  field report an agent closed a load confirmation twice, declining the load it was trying to confirm, before
  it read `pulse` and established that OK was the affirmative. The old name misdescribed the action too:
  agreeing to something read as *dismissing* it with the OK button.

  No verb guesses which button means yes, and none will. On a save prompt the affirmative is whichever of Save
  and Don't Save the caller meant, and a guess there writes or discards a user's file. `pulse` lists the
  buttons, and the caller names one.

### Deprecated

- **`dismiss` is superseded by `dialog` and still works exactly as before**, including "nothing means close".
  Its behaviour was deliberately left uncorrected, because a caller that sends nothing in order to decline
  would otherwise stop declining without warning, and would find the dialog still open. The verb will be
  removed in one or two releases.

## 0.30.0

### Changed

- **The MCP tools are now `mcp__phenome__*`, not `mcp__grasshopper__*`. Run *Pair with VS Code* again.** The
  pairing rewrites the registration in all five host files and the permission rule, and removes the old name
  as it goes. Leaving both would register two servers, start two copies of the script, and offer every verb
  twice. A host set up before this version finds no tools at all until it is paired again.

  The old name described the half that was implemented first. It stopped being accurate several releases ago:
  commands, the document, the command line, the dialogs and now the plug-ins are all answered by the Rhino
  half, in a Rhino that never opened a canvas. A prefix saying *grasshopper* on those verbs sent agents
  looking for a canvas they did not need, including one case that started Grasshopper in order to install a
  Rhino plug-in.

- **`screenshot` and `camera` no longer need Grasshopper running.** A viewport belongs to Rhino, and answering
  about it from the canvas half meant starting Grasshopper to photograph a Rhino window. Neither verb ever
  used Grasshopper; they were implemented where the server happened to be at the time. Both now answer from
  the Rhino half. A pairing where only the canvas side is current still works, because the client falls back
  to the canvas half.

### Added

- **`plugins` answers "why is my plug-in not loading".** It reports every plug-in Rhino holds a record of,
  **including the ones that are not loaded**, each with the path Rhino believes, whether Rhino considers the
  assembly managed, whether it is load protected, and the registry key. A record present with `loaded:false`
  rules out the registry in one call, which otherwise means manual `reg query` work against a plug-in that
  works. It also reports the runtime Rhino is hosting. Rhino 8 hosts two CLRs (the executable is .NET
  Framework, and there is a .NET Core half beside it), and a plug-in must match whichever one is running.
  The Rhino half answers it, and it works with no Grasshopper open.

- **`rhino_load`** loads a plug-in by id or by path to an `.rhp`, without the confirmation dialog, and will
  load again even after a previous attempt failed. Every user-installed plug-in is load protected, which
  makes the confirmation dialog the normal case. Rhino records a failed load and will not retry it, and a
  rebuild-and-load loop appears to do nothing on the second attempt. Turning load-protection prompts off
  globally is not equivalent and is to be avoided: Rhino then does not load a protected plug-in at all, and
  does not report it.

- **`restart`** ends this agent's Rhino and starts a fresh one, waiting until the link answers. It is the unit
  of iteration for plug-in work. A .NET assembly cannot be unloaded from Rhino (there is `LoadPlugIn` and no
  `UnloadPlugIn`), and a rebuilt file reaches a running Rhino only through a new process. It refuses while
  either half holds unsaved work, because the process is ended outright with no save prompt on the way out;
  `discard:true` confirms that intent. Only the process this agent is working in is ended, and a Rhino that
  another session is using is unaffected.

- **`launch` recommends `grasshopper:false` for plug-in work.** Building, installing and loading a Rhino
  plug-in needs no canvas, and starting Grasshopper for it costs a slower launch and one more program that can
  fail to start.

## 0.24.2

### Added

- **Every open document can be listed, switched to and closed.** `documents` reports each document
  Grasshopper holds open, with its id, name, path, object count and whether it has unsaved edits, and states
  which one the canvas is showing. Passing `use` points the canvas at another. `new` and `open` have always
  left the previous document **open**. It keeps its unsaved edits and becomes unreachable, because every verb
  addresses whichever document is on the canvas. Long sessions accumulated open documents that could be
  neither listed nor switched to.

  Closing is two verbs, with no flag. `close` discards whatever is unsaved. `saveandclose` writes it first,
  and refuses a document that has never been saved instead of choosing a location for it. The two verbs make
  the destructive action explicit, because a flag left at its default is indistinguishable from a deliberate
  choice. Neither shows a save prompt, because a modal dialog blocks the thread every verb needs. The verb
  chosen is the decision, and `close` reports `discardedUnsavedChanges` when something was in fact discarded.

### Fixed

- **Wires stop showing a selection that nothing could clear.** After the link placed a component, clicking
  near one of its sockets highlighted every wire into it as though the component were selected, while the
  component itself was not. Clicking, deselecting and Escape did not clear it. Creating an object required
  new attributes on a component that already had them, which left its parameters referring to an object the
  document could no longer reach. The selection landed there, out of reach as well. Existing files were never
  damaged and need nothing done to them; reopening one has always been enough.

- **A group reports its declared signature before anything is wired to it.** A group whose inlets and
  outlets had just been declared by name returned no ports, and `peek` then advised calling `signature`,
  which had just been done. An inlet holding a constant typed into its socket disappeared the same way. The
  side a port stands on is now recorded when it is created instead of being inferred from wires that do not
  exist yet.

- **The first verb after `launch` no longer fails with "There is no document".** A freshly opened Grasshopper
  holds no document at all. It shows its start screen and creates one when a component is dragged onto the
  canvas. The opening call of a session was refused for an unexplained reason, and the remedy was a verb the
  caller had to know to ask for. `add`, `place` and `group` now create a document when there is none, which
  is what Grasshopper does. Reading still reports that there is nothing there.

## 0.24.1

### Fixed

- **Pair with VS Code registers the server for every host, not only for Claude Code.** It wrote `.mcp.json`
  and nothing else, which is where *Claude Code* looks. Kilo Code, Roo Code, Cline, Cursor and VS Code's own
  MCP support each read a different file. On any of those the pairing wrote notes expressed almost entirely in
  terms of the `grasshopper` tools and registered those tools nowhere the host would read. The agent saw no
  tools at all and had no way to know why. The registration now goes into `.mcp.json`, `.kilocode/mcp.json`,
  `.roo/mcp.json`, `.cursor/mcp.json` and `.vscode/mcp.json`, merged with whatever else those files carry.
  The last of them is keyed `servers`, which is VS Code's spelling; a file keyed `mcpServers` there is
  ignored silently.

  **A workspace paired before this version needs *Pair with VS Code* run again.** The pairing is idempotent
  and will add the files that were missing.

- **The notes no longer send an agent to restart a session that was not the problem.** They said that a
  missing tool means the session predates the server and should be restarted. That is true of *one* missing
  tool and useless when there are no `grasshopper` tools at all, which means the host simply has no server
  configured. The two cases are now told apart, and the second one points at the HTTP protocol instead of at
  a restart.

- **The HTTP fallback states how to start a session.** It could not be determined from `GET /`, and there is
  no verb for it: the server runs inside Grasshopper, and nothing answers until Grasshopper runs. Both ways it
  goes wrong are now written down. Rhino started on its own writes a `phenome-rhino-*.port` and never a
  `phenome-link-*.port`. The `/runscript` argument has to be quoted as one whole token; otherwise some shells
  double the inner quotes, Rhino runs no script, and the result is indistinguishable from Rhino being slow.

## 0.24.0

### Changed

- **A refused `place` reports everything that is wrong with the recipe, not the first thing.** The recipe is
  still all-or-nothing: one bad entry means nothing is placed and the canvas is untouched. The refusal now
  names **every** entry it could not resolve, keyed by the caller's own local id. In one field report a
  thirteen-object recipe with six name collisions came back six times, once per collision, because each
  refusal named only the first. One pass of edits now fixes the batch.

- **An ambiguous component name comes back ready to paste.** The candidates were already listed, as prose with
  the guid at the end of a sentence. They now arrive as `{"name":"Merge","guid":"3cadddef-…"}` with the
  category *and* each candidate's description. Both `Merge` components are in `Sets › Tree`, and the category
  alone cannot tell them apart; a reader choosing by the label had no way to know which was which.

- **`preview` works on a single object, and on a list.** It took a group id or nothing at all, which missed
  the case that prompted it: one intermediate component flooding the viewport while the rest of its group has
  to keep drawing. A facade of 960 panels interpolated through 24 points each produced **23,040 preview
  markers** over the model, and neither the user nor any screenshot could see the product underneath. `id`
  now takes a group or an object, `ids` takes a list of either, and `on:true` restores any of them. Every id
  is checked before a single flag moves. A bad one refuses the batch, and the refusal names it.

- **`set` with `param: "preview"` names the verb that does it instead.** The refusal was accurate, since a
  Construct Point has no such parameter. The author also checked `param` and `canvas` and concluded that
  nothing could turn a preview off, while the `preview` verb had been doing it for releases. A refusal that
  leaves the reader with less information than the server has is a defect in itself.

### Added

- **The pairing notes carry each component's `ComponentGuid`.** The table is the same with one more column,
  and an agent should now place components by guid. A `.gh` file stores the guid in order to find a component
  again, which makes it the one identity that cannot drift. A plug-in author can rename a display name, a
  nickname is editable per instance, and ribbon categories get reorganised. Naming by guid also avoids the
  ambiguity refusal entirely; the definition that prompted this change got six of them.

## 0.23.0

### Fixed

- **A verb is no longer reported as failed when it actually ran.** `wire` and `set` batches could answer "Rhino
  is busy: the UI thread is working" and be applied anyway. Work is queued onto Rhino's thread, and giving up
  waiting for it did not remove it from the queue. An agent that retried on that answer applied it twice. The
  wait is now a handshake: work that has not started can be abandoned, and work that has started is waited
  for. The answer is one of three facts (it ran, it never started, or it started and is still running) and
  is never "it failed" about something that happened.

- **A client that disconnects no longer looks like a broken verb.** When an answer could not be delivered
  because the caller had gone, the write failure was treated as the verb failing: the friction log gained an
  entry for a verb that had run, the command line echoed a failure that had not happened, and the error
  handler tried to answer a second time on a closed connection, which is where "this operation cannot be
  performed after the response has been submitted" came from. In one two-agent session, **947 of 1132 friction
  entries** were this and nothing else. Delivery failures are now counted, not logged as refusals.

- **One agent's long verb no longer locks the other out.** Requests were answered on the accept loop itself.
  During a two-minute bake the next request was not even queued behind it: it was not accepted at all, and
  the second client's own timeout fired. Requests are now accepted while one is being answered. Document work
  is as serialised as it always was; what runs in parallel is the part that never needed Rhino.

- **A note's text is no longer silently dropped.** `place` read `text` for a `Panel` and ignored it for a
  `Scribble`, answering `ok` while the canvas said "Doubleclick Me!". It was reported after a user sent the
  agent a screenshot. `Scribble` now takes `text` on create and can be reworded with `set`, which is the
  repair path when the first wording was wrong. Empty or whitespace text is refused on both and does not
  become a placeholder.

- **`arrange` is idempotent.** Running it twice ran the layout again across the canvas. The layout anchors on
  the top-left of where the objects were, but inside a group the first object sits inset by the frame's
  padding and its label. Every run added that inset again, 26 by 52 pixels per run, indefinitely. It only
  appeared when the top-left-most object was in a group, and testing on ungrouped objects did not reveal it. A
  settled document now answers `moved: 0` and the coordinates do not change.

- **New objects land in free space instead of on the origin.** `add` left an object's position unset, which
  put it at 0,0, on top of whatever was already there and on top of the next object added the same way.

### Added

- **Annotations can be read back.** `describe` on a note answers its `text`, where it sits (`at`), the
  rectangle it covers (`box`) and the group it belongs to; `canvas` carries the same for every note. Until now
  a note had no readable position at all, and the agent could not verify any correction to it: it could write
  one only on trust. Placement was the part that went wrong, and a box can be compared against another box
  without looking at a screen.

- **`arrange` places notes, as captions.** The layout takes a note's group as what the note is about and needs
  no other instruction. A note in a group becomes that group's caption and is placed above the group's other
  members. A note in no group is about the whole definition and is placed above everything as a title. Before
  this, `arrange` moved every component and left the notes where they were, and a scribble could end up lying
  across the sliders it described. There is nothing new to pass: an author already says which kind of note it
  is by giving `place` a `group` or not.

- **Notes appear in the mermaid diagram.** `canvas` with `as:'mermaid'` renders each one as `[/"the text"/]`,
  inside its group's subgraph or loose at the top level. Reading a definition back returns its comments as
  well as its wiring.

- **`group` creates declared ports on a group that already exists.** Calling it again with `inlets` or
  `outlets` used to accept them and do nothing, and a group could not be given a signature after the fact.
  Missing ports are now created, matched by nickname, and calling it twice adds nothing the second time.

### Changed

- **The request echo in Rhino's command line is bracketed, and carries the whole address.**

  ```
  [00:20:12] [127.0.0.1:53911] [78 ms] new
  [00:20:26] [127.0.0.1:53911] [14 ms] place  !!  'Addition' names 2 different components
  ```

  Each line has three bracketed fields and then the verb: when, from where, how long, and what. The verb comes
  last because it is the only field whose width varies and the only field being scanned *for*; a variable
  field in the middle shifts the columns after it. The address carries the host as well as the port, and a
  line can be pasted into a request without being assembled first. The duration is not padded: aligned digits
  are useful in a column of four-digit numbers and read as a gap where almost every line is two digits of
  milliseconds, and the brackets already provide the alignment.

- **A release carries the plug-in files, not a Yak package.** There is no public package server to publish to,
  and the package was being built and attached with no consumer. Installing it still meant downloading a file
  and running `yak install` by hand, which is no easier than placing the `.gha` and `.rhp` in the Grasshopper
  components folder. The build no longer produces one. `tools/pack-yak.ps1` is still there and still works for
  anyone distributing the package themselves.

## 0.22.0

### Changed

- **An agent's edit marks the document modified, like a user's edit.** Closing Rhino then offers to save, and
  the Grasshopper title carries the usual asterisk while there is work outstanding. Until now the link changed
  a document and left the flag alone. Rhino closed it without asking, and an agent's work could be lost with
  no prompt.

  Reading, selecting and zooming never mark it. `arrange`, `signature` and `preview` mark it only when they
  actually change something. All three are finishing steps that are commonly run more than once, and a save
  prompt for a repeated no-op teaches users to dismiss the prompt without reading it.

- **The request echo in Rhino's command line reads as columns, and carries the port.** The duration's number
  and its unit are separate columns, and a slow call is found by the width of a number without reading.
  `ok` is not repeated on every line; only the failures are marked. The port is on every line because the
  banner that carried it has scrolled off the top by the fifteenth request. Any screenshot of the log now
  identifies the session.

### Added

- **`escape`** cancels whatever Rhino is waiting for. It covers the case `dismiss` cannot answer: a command
  waiting on a pick is not a dialog. Nothing is disabled and there is no window to click, yet the thread is
  held and other verbs report *busy* although waiting has no effect.
- **`camera`** reads or aims the active viewport. Rhino's own `Zoom` is interactive, and scripting it waits
  for a pick that never arrives, which blocks the UI thread.
- **`plugins`** reports what is loaded, with versions and origin. It is for the case where a console message
  names a plug-in and attributing it would otherwise require starting a second Rhino.
- **`sessions` with `use` and `release`** pins one canvas. With two Rhinos open the choice was previously made
  by whichever answered first, and an agent could end up editing the canvas the user was not viewing.
- **`console?mine=true`** returns the link's own lines, which `console` omits so that an agent does not read
  its own requests as Rhino's answers. It is needed when the suspicion is that the bridge, and not Rhino, is
  at fault.

### Fixed

- **Saving through the link left the Grasshopper window saying "unnamed".** The title was cached and rebuilt
  from five places, none of which a save through the link reached. It now shows the file's name, and the save
  clears the modified flag. Rhino no longer offers to save what was just saved.
- **A group at the end of a definition reported no outlets.** An outlet was determined by "has a recipient
  outside the group", and a terminal group has none because it is the result. As a result `peek` hid the values
  worth reading, and the whole-document `preview` sweep darkened the geometry it exists to leave drawing.
- **Data mapping was stored and ignored.** `param` set flatten, graft, simplify or reverse and the tree came
  through unchanged or, on an output, stopped coming through at all. Both sides now take effect on the next
  read.
- **`arrange` is idempotent.** It reported every object as moved even on a settled layout, and pushed an undo
  step per object that undid nothing. Running it twice is normal; the second run now reports nothing and
  records nothing.
- **Rhino can be closed through the link.** Closing a document with unsaved changes stops on Grasshopper's
  multi-save prompt, and by then Rhino has destroyed its own frame. `pulse` reported *busy, working on
  something unnamed* while a clickable dialog blocked the exit. The dialog is now named with its buttons
  listed, and `dismiss` ends the process cleanly. Separately, every WinForms button was invisible to the button
  scan, and Grasshopper's dialogs are WinForms.
- **`/pulse` reports `clickable` from the Rhino plug-in too.** It previously came only from the canvas plug-in,
  while both halves' protocol text told callers to read it. The Rhino half exists for the dialogs where it is
  false.
- **`dismiss` honours `key` in the Rhino plug-in too.** It was read and discarded there.
- **Two Rhinos at once no longer collide.** The friction log stopped losing entries, and two starting together
  can no longer claim the same port and leave the loser advertising one with no listener.
- **`bake` states why it did nothing** instead of answering `baked: 0` with no reason. **`describe` reports** a
  component's own runtime messages, `enabled` and `drawing`. **`place` rolls back** everything it added when a
  later step fails and leaves no orphans on the canvas.
- **Leftover files are removed at startup**: port files whose Rhino is gone, and autosaves older than a week.
  The cleanup runs at startup because exit does not always happen.

## Before 0.22.0

Earlier releases are not reconstructed here, because notes written after the fact are worth less than the
record that already exists. `git tag` lists the releases and each is a commit that states what it was for:
`v0.21.1` and `v0.21.0` were the Rhino plug-in actually reaching the package, and `v0.20.0` was that plug-in
arriving at all. With it, Rhino could say what it was doing and be answered while the canvas could not.
