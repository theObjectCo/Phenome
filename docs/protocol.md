# The protocol, for writing another client

The MCP server in the extension is one client of a plain HTTP API. This page describes that API closely
enough to write another client: a script, a different editor, or an agent harness that does not run Node.

**`GET /` is the authority, not this file.** It lists every verb with its arguments and its answers. It is
written by hand next to the dispatch table in `LinkServer.cs`, and a new verb is added to both in the same
change. This page covers what a one-line description per verb cannot state: the session model, the rules the verbs share, and the
half-dozen behaviors that are otherwise costly to rediscover.

## Finding a session

Each plugin binds an **ephemeral loopback port** at startup and writes it to a file named by Rhino's
process id:

```
%TEMP%\phenome-link-<rhino pid>.port         the canvas: the document and the verbs that edit it
%TEMP%\phenome-rhino-<rhino pid>.port        the process: whether it is free, what blocks it, and the
                                             verbs that need Rhino but not a canvas
%TEMP%\phenome-rhinoinside-<pid>.port        the headless core: a Rhino in a process of its
                                             own, reading and converting files on disk
```

Those files are the whole of discovery. There is no registry, no daemon and no fixed port. Each file name
says which server wrote it. The first two share a pid because they are two servers in one Rhino, and the
third is its own process.

**There are two servers on purpose.** They answer about different things and are available at different
times. The canvas server lives in a `.gha`, which is not loaded until Grasshopper has been started; the
process server lives in a `.rhp` that loads with Rhino. A client trying to determine why nothing is
answering must ask the server that still can. Matching the pid in the two filenames identifies the correct
pair.

- **Each Rhino has its own file.** Several Rhinos can run at once, each with a separate canvas and a
  separate journal. An agent that should stay on one canvas holds on to one port for the session.
- **A stale file has a dead pid.** A plugin deletes its own file on shutdown and deletes other plugins'
  stale files at startup. The sweep runs at startup because shutdown does not always run, and a client
  that trusts a port without checking the pid can still connect to a dead process. Check the process before
  trusting the port.
- **No file means no session.** Rhino is not running, or Grasshopper was never opened, or the `.gha` did
  not load. On a fresh install the third is most likely, and the cause is almost always a blocked assembly
  (see the README's install notes). A `phenome-rhino-*.port` without a `phenome-link-*.port` narrows it in
  one step: Rhino is up and Grasshopper has not been opened.

Every address is `http://127.0.0.1:<port>`, and the listener binds nothing but loopback. The answers carry
no `Access-Control-Allow-Origin` header. A page the user visits can reach loopback, and without that header
the browser hands the page an opaque response it cannot read.

**Starting a session is not a verb.** The server lives inside Grasshopper and cannot answer until
Grasshopper is running. The MCP layer carries a `launch` tool for this, and the protocol has no
equivalent. A client that speaks only HTTP cannot discover this one operation from `GET /`:

```
Rhino.exe /nosplash /runscript="_Grasshopper"
```

Both parts of that command line matter. **Rhino alone gives no canvas link.** The `.gha` loads with
Grasshopper, and a Rhino started without that script writes a `phenome-rhino-*.port` and never a
`phenome-link-*.port`. A client should recognise that pair as the symptom and not wait it out.

**The quotes go around `_Grasshopper`, not around the whole argument.** These results were measured on
Rhino 8, with each form launched the same way:

| argument | result |
|---|---|
| `/runscript="_Grasshopper"` | Grasshopper opens, port file within about six seconds |
| `"/runscript=_Grasshopper"` | Rhino starts, no canvas, no port file, ever |
| `/runscript=_Grasshopper` | the same nothing |

Earlier versions of this page recommended the second form. The failure it produces looks the same as Rhino
being slow to start, and it is easily misread as a slow start and waited out. Whatever launches Rhino must
also pass the argument through untouched. A launcher that re-quotes produces one of the other two forms,
and the `launch` tool sets `windowsVerbatimArguments` to prevent that.

Then poll for a port file that was **not** there before starting. Rhino can take tens of seconds.
Attaching to a port that already existed puts two clients on one canvas, which is worse than not starting.

## One JSON in, one JSON out

Reads are `GET`, and writes are `POST` with a JSON body. There is no content negotiation, no versioning
header and no session token.

| status | meaning | body |
|---|---|---|
| `200` | it worked | the verb's own answer |
| `403` | the request looks like a browser's, or this build has been withdrawn | `{"ok":false,"error":"<which of the two, in words>"}` |
| `404` | no such verb, or no such object or parameter | `{"ok":false,"error":"There is no POST /wibble. GET / describes what there is."}` |
| `500` | the verb refused, or failed | `{"ok":false,"error":"<what went wrong, in words>"}` |

**Since 0.32.0 the link refuses anything that looks like it came from a web page.** A page the user visits
can reach `127.0.0.1`. Its request comes from the user's own machine and cannot be told apart from a local
client's, and this API runs code in Rhino. Loopback alone is therefore not a sufficient boundary. The link
answers `403` to any request carrying `Origin`, `Referer`, `Sec-Fetch-Site` or `Sec-Fetch-Dest`, and to one
whose `Host` is present and is not this link's own address (`127.0.0.1`, `localhost` or `[::1]` with its
port). Node's `fetch` sends `Sec-Fetch-Mode`, and that header is allowed.

**Every `POST` must also carry `Content-Type: application/json`.** This is the check that reliably holds.
A `no-cors` request may use `text/plain`, `x-www-form-urlencoded` or `multipart/form-data` and nothing
else. Asking for JSON makes the request non-simple, and the browser sends a preflight first. A server that
does not answer preflights with permission stops the request in the browser, and the server never receives
the POST. The header checks above hold only while browsers keep sending their current headers, and this
one holds because a page cannot satisfy it.

**Set the content type, and none of the browser headers.** A client running *inside* a browser cannot talk
to this link at all. That is deliberate and will not be relaxed. There is no allow-list and no token to ask
for. A program that is not a browser already passes, and a browser cannot pass.

**Most `500`s are deliberate refusals.** `/delete` refuses when it would cut live wires, and `/signature`
refuses when an object belongs to two groups. The message says which, in sentences written for a person to
read. A client should match on behaviour and never on the wording.

**Every refusal is written to the friction log** with the request that caused it, and `GET /friction`
reads it back. A refused call needs no report; report the calls that *succeeded* and did the wrong thing,
with `POST /report`.

## The journal records every change

Every change appends one entry. There is no push, no subscription and no websocket: a client asks for
everything after the last sequence number it saw.

```
GET /events?since=0
```

```json
{
  "latest": 42,
  "events": [
    { "seq": 41, "at": "14:03:11", "author": "you",   "kind": "message", "text": "make the legs parametric" },
    { "seq": 42, "at": "14:03:24", "author": "claude", "kind": "place",   "count": 6 }
  ]
}
```

- **`latest` is the next cursor.** Use it verbatim instead of reading the last entry's `seq`. While nothing
  is happening the `events` array is usually empty, and then there is no last entry to read.
- **`since` is exclusive** and defaults to `0`, which means everything still held.
- **Ten clients cost the server what one does.** The server keeps no state per client, and polling every
  second or two is the intended usage.

### Handling the gap

The journal keeps the **last 10 000 entries** and drops older ones from the front. The cap bounds memory
use and does not guarantee history.

If the first `seq` in an answer is **greater than `since + 1`**, entries were dropped between the client's
cursor and now. The client has missed changes and cannot reconstruct them. **Re-read `/canvas`** and carry
on from the new `latest`, without trying to interpolate.

This is rare in a paired session and common after a client has been idle for an hour.

### Authorship, and skipping a client's own echo

Put the client's own name in `author` on every `POST`. The server defaults it to `"unnamed"`. That works,
but every entry then appears to come from the same anonymous author and the journal becomes unreadable.

Entries carry the author back. **Skip entries written under the client's own name.** An agent that does
not skip them reads its own `place` as new information and reacts to it, which produces a feedback loop
between the two clients.

The user's messages arrive as `kind:"message"` with the user's author name. Answer with `POST /say`
(`{author, text, to?}`).

## Rules the verbs share

**Batch every call.** `/wire` takes `wires:[…]` and `/set` takes `values:[…]`. `/place` takes an entire
group body in one call: components, their wires and their typed-in values. A loop of single calls is slower
by orders of magnitude, because each call crosses to the UI thread and back.

**Calls serialise on Grasshopper's UI thread.** Requests are answered on worker threads, and anything that
touches the document is marshalled onto the UI thread, where a slow verb blocks the others. Concurrent
requests give no speedup here, and batching is what improves throughput.

**Ids are Grasshopper instance GUIDs** and stay the same for the life of an object. `/canvas` lists them,
`/describe?id=` answers one object's real parameters, and `/peek?id=` answers a parameter's full data with
tree paths.

**A `ComponentGuid` is the only stable way to name a *kind* of component**, and `/place` takes one in
`guid`. A display name is unstable: a plugin author may rename a component between releases, and names
collide across plugins. With a normal set installed, `Addition`, `Merge`, `Scale`, `Rotate` and `Area`
each name more than one. The link refuses an ambiguous name and does not guess. The wrong choice (a vector
`Addition` in place of the scalar one) surfaces several groups later as a type-conversion error that is
hard to trace. The refusal returns each candidate as a paste-ready literal,
`{"name":"Merge","guid":"3cadddef-…"}`, with its category and its own description. The category alone does
not always separate the candidates: both `Merge` components live in `Sets › Tree`.

**A refused `/place` names every entry that failed, not only the first.** The recipe stays atomic: with
one bad entry nothing is placed and the canvas is untouched. The refusal lists each unresolved entry under
the recipe's own local `id`, and one pass of edits fixes the batch. In a measured case, reporting only the
first failure turned a thirteen-object recipe with six collisions into six round trips.

**`/peek` on a group id answers that group's signature instead.** The answer lists every inlet and outlet
with its name, type, branch and item counts, and a few values from each outlet:

```json
{ "ok": true, "group": "Steps",
  "inlets":  [ { "name": "Count",  "id": "…", "type": "Integer",      "count": 1, "branches": 1 } ],
  "outlets": [ { "name": "Result", "id": "…", "type": "Generic Data", "count": 1, "branches": 1,
                 "sample": ["55"] } ] }
```

A group is a function, and this answer is its type as it stands. Assert against it after editing the
group. The answer carries counts instead of full data, because six outlets with a thousand branches each
would overflow the context `/peek` is meant to keep small. For the values, peek at a port's `id`.

Direction is derived from the wires and is not stored. A port fed from outside the group is an inlet, and
one read from outside is an outlet. The `/signature` verb plants ports by the same rule, and the two cannot
disagree. Ports an author placed by hand count too. A group with neither answers empty arrays and a `note`
saying it has no signature yet.

**Flags are read loosely.** `true`, `"true"` and `1` all mean true. MCP clients routinely serialise scalars
as strings, and a server that required a real boolean would reject valid requests from those clients.

**A refusal means the verb did not happen. After a transport failure, whether it happened is unknown.** The
protocol defines that distinction; it is more than a convention. The verbs are not idempotent: `wire`,
`set` and `place` sent twice make two of everything.

- A JSON body with `ok:false` is authoritative: the verb refused. A refused `/place` applied nothing. A
  batch of `/wire` or `/set` keeps the elements applied before the one that failed.
- `"Rhino is busy: the UI thread is working"` means the work never started, and it is safe to send again.
  Ask `/pulse` first: it answers while the thread is held and distinguishes a long solve from a modal.
- `"This started and has not finished after 5 minutes"` means the work started. It is running and will
  finish. **Do not send it again.**
- No answer at all (a timeout, a dropped socket) says nothing about whether the verb ran. **Do not retry.**
  Read `/events`, which is always a correct extra call, and look for an entry under the client's own
  `author`. Every mutating verb journals one, and the journal is the record of what landed.

Work is queued onto Rhino's UI thread, and abandoning the wait cannot unqueue it. The wait is therefore a
handshake: pending work can be abandoned, and started work is waited for. The handshake is what makes the
three cases above reliable. An answer that cannot be delivered because the caller has gone is counted and
otherwise ignored, and the verb itself has run.

**Notes carry their text and their position.** A `Scribble` and a `Panel` both take `text` on `place` and
can be reworded with `/set`. Empty or whitespace text is refused, and the link never substitutes a
placeholder for it. `/describe` answers `annotation: {kind, text, at, box, group, groupName}`, and `/canvas`
carries the same for each note. Wording *and* placement can be checked without a screenshot.

**A panel sends its text as one item unless the text is given as an array.** Grasshopper's Multiline Data
flag decides this, and it is on for every new panel, including one dragged off the ribbon. With the flag on,
`"-3\n3\n3\n-3"` arrives downstream as one piece of text. Given as an array of strings, `text` on `/place` or
`value` on `/set` turns the flag off and writes one line per element, and the panel sends a list. A string
leaves the flag as it was. `/canvas` reports `multiline` for every panel.

**A parameter stores a list when `value` is an array.** On `/set` and on `/place`, an array stores one item
per element, and two or three numbers in an inner array are a point: a Point parameter takes
`[[0,0,0],[10.35,0,0]]` or `["0,0,0","10.35,0,0"]`. A value the parameter cannot read refuses the entry and
leaves the socket as it was. The link never stores fewer items than were sent. `null` or `[]` empties it.

**`/set` also renames a parameter and sizes a panel.** `nickname` renames a parameter standing on its own,
such as a group's inlet or outlet, a slider or a panel, and is refused on a component. `width` and `height`
size a Panel, starting at 20. With any of the three, `value` may be left out.

**Colours are read by Grasshopper's own parser.** `/set` on a Colour Swatch takes `[r,g,b]`, `[r,g,b,a]` or
text. Four numbers are r,g,b,a with the alpha last. Eight hex digits are `#aarrggbb` with the alpha first,
which is .NET's order and the reverse of CSS. Any other count of numbers is refused, and a name has to be
one the system knows.

**`/arrange` stacks sources in the order of the sockets they feed.** Within a column, a block feeding a
component's first input stands above one feeding its second, and the groups feeding another group stand in
the order of its inlets. A block with nothing upstream goes in the column just left of its nearest reader.
Blocks with no wire to order them keep their vertical order. A second `/arrange` answers `moved: 0`: pivots
are planned in whole pixels, and an object within a pixel of its place is not moved.

**A note's group decides where `/arrange` puts it.** Notes have no ports and no dataflow and are not part
of the layout algebra. A pass of their own lays them out after the components have their positions. The
rule needs no new field, because a note's group already says which case applies. A note **in a group** is
that group's caption and goes above the group's other members. A note **in no group** is about the whole
definition and goes above everything as a title.
`/place` sets this through its `group` field, and `/describe` reports it back. Repeated `/arrange` calls
land on the same coordinates.

A scribble is a single unwrapped line. A caption longer than the components it sits over makes the frame
around its group wider than the room the layout reserved, and two group frames can then touch. `/arrange`
does not clear that, because every block is already at its final position. `/review` reports the case, and
the finding says to shorten the note.

**`/preview` takes a group id or an object id, and `ids` for a list of either.** The one verb works at
three granularities. With no id it sweeps the document on the colour rule: only the outlets of the red and
yellow groups keep drawing, and objects in no group are quieted with the rest. A group id quiets that group
on its own terms, whatever its colour, and an object id quiets exactly that object. `on:true` turns drawing
back on for any of them. The answer carries a `groups` array and an `objects` array, each with what ended up
drawing as opposed to what this call changed, and a sweep adds `ungrouped` with the same three counts.
Every id is checked before any flag moves. An id that is not on the canvas refuses the whole list and is
named in the refusal. An object that draws nothing is skipped and listed under `skipped`, and the rest of
the list goes ahead. On a canvas with no groups at all the sweep quiets everything.

Object ids were added for a case group ids could not cover: an intermediate component flooding the viewport
while the rest of its group has to keep drawing. The report came from a facade of 960 panels interpolated
through 24 points each, which put 23,040 preview markers over the building and left no usable view. One
verb serves both kinds of id, because a group and an object differ only in the policy over their members,
and only the sweep has a policy. With a second verb, every caller would have to know which verb an id needs
before asking.

Drawing is not a parameter, and `/set` with `param: "preview"` now says so and names this verb. An earlier
version refused accurately but gave no pointer, and the author concluded the change was impossible.

**Since 0.22.0 an edit marks the document modified, and closing Rhino offers to save it.** Before 0.22.0
the link changed a document and left `IsModified` false. Rhino then closed it without asking, and the user
lost an agent's work with no prompt. `/canvas` reports `modified` and `path`, which lets a client read the
state instead of inferring it, and `/save` clears the flag.

Reading never marks the document, and neither do `/select` or `/zoom`. `/arrange`, `/signature` and
`/preview` mark it only when they actually change something. All three are often run more than once, and a
save prompt after a run that changed nothing trains users to dismiss the prompt without reading it.

**Verify numerically.** `/peek` returns branch and item counts with paths, and those counts are the
specification. A screenshot shows only that a definition looks plausible. `/canvas-image` and
`/screenshot` exist for the user's half of the pairing.

`/measure?id=` answers the sizes `/peek` does not: per item, a curve's length and, when it is closed and
planar, its area; a brep's or mesh's area and, when closed, its volume; and totals with a bounding box. It
reads a component's output unless `side=input`. With `against=` (and `againstSide`, `againstParam`) it
compares every pair from the two sets: the area two closed planar curves share, the volume two solids share,
which pairs overlap, and the nearest distance between curves or points. The same id and parameter twice
compares a set with itself, each pair once. A call compares at most 2,500 pairs, because each boolean runs on
Rhino's UI thread. The verb was added after a session measured profile overlaps with a script component that
it created and deleted once per variant across 64 variants.

`/screenshot` redraws the view off screen at the requested size, and geometry a plug-in draws with its own
display code is not always in that redraw. Two cases are on record: an off-thread volume preview came back as
a cropped piece of an older frame, and a script component's curves were missing from four captures while
they showed on the user's screen. When `/peek` reports the geometry and the capture does not show it,
`/peek` is right.

**`/canvas-image` does not show anything painted onto a control.** It re-renders the document to a bitmap
instead of capturing the window, and overlays drawn during the canvas paint do not appear in it; only the
screen shows them.

## When nothing answers

Every verb above needs the UI thread, and when that thread is held they all time out together. The process
server reports which of two opposite situations Rhino is in, and it never touches the UI thread itself.

**`GET /pulse`** answers `idle`, `busy` or `blocked`. An idle handler stamps the time whenever the UI
thread has nothing to do, and a stale stamp means the thread is not free. The command events say what is
running; they are cached as they fire and not asked for on demand. Windows shows whether a modal is up,
because it disables the owner window while one is open.

```json
{ "ok": true, "state": "blocked", "uiFree": false,
  "dialog": { "present": true, "title": "Explode Large Mesh",
              "buttons": ["Yes", "No", "Cancel"], "clickable": true },
  "advice": "The dialog \"Explode Large Mesh\" is open. Nothing will answer until someone clicks it." }
```

A stale stamp with a command running means *wait*. A stale stamp with a dialog up means *stuck*, and the
answer names the dialog.

**`POST /dismiss`** answers that dialog. `{button}` presses a button by name, `{key}` types a key instead,
and a request with neither closes the dialog. Closing is the default because closing is what the X does,
and the X declines. `{expect}` names the dialog the client meant to answer, and the call refuses if another
is up by then. Dialogs are transient, and a blind press answers whatever happens to be there.

**`clickable: false` does not mean an empty list of buttons.** It means the dialog draws its own buttons, as
Rhino's Eto prompts do. Those buttons are not windows, and there is nothing to post a click to. Send a key
with `{key}`. `WM_CLOSE` is no substitute, because on a *save changes?* prompt closing means cancel and the
intended action does not happen.

**`POST /escape`** covers the case `/dismiss` leaves. A command waiting on a pick is not a dialog. Nothing
is disabled, there is no window to enumerate, and `/dismiss` correctly refuses, yet the UI thread is held
all the same and every other verb answers *busy*. Running an interactive command from a script is the usual
way to reach this state. `{times}` cancels that many levels. The default is one and the cap is five,
because repeated Escapes in an idle Rhino clear a selection the user wanted. The key is queued and the
answer does not confirm its delivery. Check `/pulse` afterwards instead of trusting the answer.

**At shutdown**, closing a document with unsaved changes stops on Grasshopper's multi-save prompt, and by
then Rhino has already destroyed its own frame. A diagnosis that asks the operating system which window is
the main one gets the prompt itself, sees an enabled window and reports *busy, working on something
unnamed*. Since 0.22.0 the frame is remembered from the first idle, a destroyed frame stays destroyed, and
the prompt is named with its buttons listed. `/dismiss {button:"Close"}` then ends the process cleanly.
Before 0.22.0 this required direct Win32 handling.

**`GET /console?tail=50`** answers the tail of Rhino's own command line, where commands and scripts reply.
Before this verb only the user could see it. The command line is drained when the UI thread is idle, and a
long script's output arrives in one piece when the script ends; while it runs, use `/pulse`.

**There is one capture per Rhino, and the process server owns it.** `CapturedCommandWindowStrings` clears
the buffer as it reads. Two drains do not double the lines; they split them, each taking a different
portion. The `.rhp` loads before any canvas exists and starts the drain. The canvas server finds capture
already on, does not start a second, and answers `/console` by reading the process server over loopback.
When only the `.gha` is installed, it drains its own buffer.

**`?mine=true` answers the link's own lines instead.** The link's own output is filtered out of `/console`,
and an agent does not read its requests back as Rhino's answers. The same filter hid the bridge's own errors
from a client reading through the bridge, which is when those errors are most needed. Those lines are kept
in a separate ring buffer and served on request.

### Working in a Rhino with no canvas

The process server also runs the verbs that need no definition: `/command`, `/doc`, `/plugins`, `/load`,
`/screenshot`, `/camera` and `/console`. A Rhino started without Grasshopper is
therefore still a session an agent can work in: open a file, select, run a command, export, read what Rhino
said.

**`POST /command`** takes `{script}` and runs it as a Rhino command script. A leading `-` runs a command
without its dialogs. **`GET /doc`** answers the document: its name, whether it is modified, the layers with
their visibility and locks, the object count, and where the camera is.

Both are commands, and commands run on the UI thread. Unlike `/pulse`, they time out when that thread is
held. The timeout names the situation in the same wording as `/pulse`.

The canvas server answers these two as well, at `GET` and `POST /rhino`, and will go on doing so. A client
that finds a `phenome-rhino-*.port` should prefer it, because those verbs are there whether or not
Grasshopper was ever opened. A `404` from an older `.rhp` is the signal to fall back to the canvas server
and keep going. One half of a pairing is often updated before the other.

### A headless Rhino

The third server is a program and not a plugin. It starts a Rhino core in its own process with
`WindowStyle.NoWindow` and answers about files on disk, since there is no open document. It follows the same
conventions (loopback, one JSON out, an ephemeral port in `%TEMP%\phenome-rhinoinside-<pid>.port`), and the
same client code reaches it.

**`GET /doc?path=`** describes a `.3dm`: units, tolerance, layers, and a count of each kind of object
instead of a line per object. **`POST /convert`** takes `{from, to, version?}` and writes the format the
target's extension asks for. A `.3dm` goes through the archive writer, and anything else through Rhino's
exporter for that format. **`GET /pulse`** says whether the core is free and which verb it is on. It is
answered without the work queue and responds while the queue is busy. **`POST /quit`** ends the process.

**Rhino commands do not run there, as measured.** `RunScript` returns `false` and changes nothing. It was
tested with the serial-number overload against a headless document, and against one opened the ordinary
way, which in a windowless process is headless anyway. Commands are therefore out of reach, including
selection, export option dialogs and most of what a toolbar does. Those belong to the process server inside
a real Rhino. Reading, writing, and Rhino's importers and exporters do work, because they load in a Rhino
with no window; this is verified for `.stl`, `.obj`, `.dxf` and `.step`.

**A `NoWindow` core can still raise a modal.** Asking for file version 7 on a document holding Rhino 8 data
raises one, with three buttons and no user to select among them. The write returns false and the thread
blocks. Every write from that server therefore sets `SuppressDialogBoxes` and `SuppressAllInput`.

## What is deliberately not here

**There is no authentication.** The socket is loopback-only and the trust boundary is the machine. Where
that threat model does not fit, do not expose the port.

**There are no transactions.** Only `/place` is atomic in itself: a recipe lands whole or not at all. A
batch of `/wire` or `/set` applies its elements in order and keeps those before a failure, and a sequence of
verbs is never atomic. After a partial result, the journal says what landed and `/canvas` says what exists. Reconcile against those two,
never against the expected result.

**There is no schema version.** `GET /` describes the protocol at run time. A client that reads it at
startup adapts, and a client that hard-codes the verb list breaks when the list changes.

**There are no composition rules.** How to build a *good* definition (groups as functions with signatures,
four role colours, data on wires and never as text) is a different subject and lives with whoever builds.
The VS Code extension plants those notes as `AGENTS.md` in the workspace it pairs with.
