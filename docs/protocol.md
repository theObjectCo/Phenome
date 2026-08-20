# The protocol, for whoever writes the next client

The MCP server in the extension is one client of a plain HTTP API. This describes that API's shape well
enough to write another — a script, a different editor, an agent harness that would rather not run Node.

**`GET /` is the authority, not this file.** It is generated from the server's own dispatch table, so it
lists every verb, its arguments and its answers, and it cannot drift from what the server does. What follows
is the part a generated description cannot tell you: the session model, the rules the verbs share, and the
half-dozen things that will otherwise cost you an afternoon.

## Finding a session

Each plugin binds an **ephemeral loopback port** at startup and writes it to a file named by Rhino's
process id:

```
%TEMP%\phenome-link-<rhino pid>.port         the canvas: the document and the verbs that edit it
%TEMP%\phenome-rhino-<rhino pid>.port        the process: whether it is free, what blocks it, and the
                                             verbs that need Rhino but not a canvas
%TEMP%\phenome-rhinoinside-<pid>.port        a Rhino nobody opened: a headless core in a process of its
                                             own, reading and converting files on disk
```

Those files are the whole of discovery. There is no registry, no daemon and no fixed port. The names differ
so that finding one tells you what you found; the first two share a pid because they are two servers in one
Rhino, and the third is its own process.

**Two servers, on purpose.** They answer about different things and, more to the point, they are available
at different times. The canvas server lives in a `.gha`, which does not exist until Grasshopper has been
started; the process server lives in a `.rhp` that loads with Rhino. A client that wants to know why
nothing is answering has to ask the one that still can - and matching the pid in the two filenames is how
it finds the right pair.

- **One file per Rhino.** Several can run at once, and each is a separate canvas with a separate journal.
  An agent that should stay on one canvas holds on to one port for the session.
- **A stale file has a dead pid.** Check the process before trusting the port. A plugin deletes its own file
  on the way out and sweeps other plugins' dead ones when it starts — start rather than exit, because exit
  is precisely the moment that does not always happen — but a client that trusts a port without checking the
  pid will still eventually talk to nothing.
- **No file means no session** — Rhino is not running, or Grasshopper was never opened, or the `.gha` did
  not load. On a fresh install the third is most likely, and the cause is almost always a blocked assembly
  (see the README's install notes). A `phenome-rhino-*.port` without a `phenome-link-*.port` narrows it in
  one step: Rhino is up and Grasshopper simply has not been opened.

Everything is `http://127.0.0.1:<port>`. The listener never binds anything but loopback. `Access-Control-Allow-Origin`
is `*`, which is safe for exactly that reason: the clients are local windows, and nothing off the machine
can reach the socket in the first place.

**Starting a session is not a verb, and cannot be.** The server lives inside Grasshopper, so nothing can
answer until Grasshopper is running — which is why the MCP layer carries a `launch` tool and the protocol
does not. A client that speaks only HTTP therefore has one thing it cannot discover from `GET /`, and this
is it:

```
Rhino.exe /nosplash "/runscript=_Grasshopper"
```

Both parts matter. **Rhino alone gives you no canvas link** — the `.gha` loads with Grasshopper, so a Rhino
started without that script writes a `phenome-rhino-*.port` and never a `phenome-link-*.port`, which is the
symptom to recognise rather than wait out. And **the whole argument is quoted as one token**: an inner
`"_Grasshopper"` has its quotes doubled by some shells and process launchers, after which Rhino runs no
script at all and the failure looks identical to Rhino being slow.

Then poll for a port file that was **not** there before starting — Rhino can take tens of seconds, and
attaching to a port that already existed puts two clients on one canvas, which is worse than not starting.

## One JSON in, one JSON out

`GET` for reads, `POST` with a JSON body for writes. No content negotiation, no versioning header, no
session token.

| status | meaning | body |
|---|---|---|
| `200` | it worked | the verb's own answer |
| `404` | no such verb | `{"ok":false,"error":"There is no POST /wibble. GET / describes what there is."}` |
| `500` | the verb refused, or failed | `{"ok":false,"error":"<what went wrong, in words>"}` |

**A refusal is not a crash.** Most `500`s are deliberate: `/delete` refuses when it would cut live wires,
`/signature` refuses when an object belongs to two groups. The message says which, and it is written for a
reader rather than for a log parser — match on behaviour, not on wording.

**Every refusal logs itself** to the friction log, with the request that caused it. `GET /friction` reads it
back. You do not need to report a refused call; report the ones that *succeeded* and did the wrong thing,
with `POST /report`.

## The journal is how anyone sees anything

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

- **`latest` is your next cursor.** Use it verbatim rather than reading the last entry's `seq` — an empty
  `events` array is the common case while nothing is happening, and then there is no last entry to read.
- **`since` is exclusive** and defaults to `0`, which means everything still held.
- **Ten clients cost what one does.** No server-side state per client, so polling every second or two is
  the intended usage rather than a tolerated one.

### The gap, which you must handle

The journal keeps the **last 10 000 entries** and drops from the front. That cap is a courtesy to memory,
not a promise.

So: if the first `seq` you get back is **greater than `since + 1`**, entries were dropped between your
cursor and now. You have missed changes and cannot reconstruct them. **Re-read `/canvas`** and carry on from
the new `latest` — do not try to interpolate.

This is rare in a paired session and routine after an agent has been asleep for an hour.

### Authorship, and skipping your own echo

Put your own name in `author` on every `POST`. The server defaults it to `"unnamed"`, which works and makes
the journal useless — every entry looks like it came from the same anonymous somebody.

Entries carry the author back, so **skip entries whose author is you**. Without that, an agent reads its own
`place` as news, reacts to it, and the two of you talk past each other.

The human's messages arrive as `kind:"message"` with the human's author name. Answer with `POST /say`
(`{author, text, to?}`).

## Rules the verbs share

**Batch, always.** `/wire` takes `wires:[…]` and `/set` takes `values:[…]`. `/place` takes an entire group
body — components, their wires and their typed-in values — in one call. Loops of single calls are slower by
orders of magnitude, because each one crosses to the UI thread and back.

**Calls serialise on Grasshopper's UI thread.** Requests are answered on worker threads, but anything
touching the document is marshalled onto the UI thread, so a slow verb blocks the others. Concurrency buys
you nothing here; batching buys you everything.

**Ids are Grasshopper instance GUIDs**, stable for the life of an object. `/canvas` gives them, `/describe?id=`
tells you one object's real parameters, `/peek?id=` gives a parameter's full data with tree paths.

**A `ComponentGuid` is the only stable way to name a *kind* of component**, and `/place` takes one in `guid`.
A display name is not: a plugin author may rename it between releases, and names collide across plugins —
with a normal set installed, `Addition`, `Merge`, `Scale`, `Rotate` and `Area` each name more than one. An
ambiguous name is refused rather than guessed at, because a quietly chosen vector `Addition` in place of the
maths one surfaces several groups later as a type-conversion error and is miserable to trace. The refusal
hands back each candidate as a paste-ready literal — `{"name":"Merge","guid":"3cadddef-…"}` — with its
category and its own description, because the category alone does not always separate them: both `Merge`
components live in `Sets › Tree`.

**A refused `/place` names every entry that failed, not the first.** The recipe stays atomic — one bad entry
and nothing is placed, the canvas untouched — but the refusal is a full account: each unresolved entry keyed
by the recipe's own local `id`, so one pass of edits fixes the batch. Reporting only the first turned a
thirteen-object recipe with six collisions in it into six round trips, which was measured rather than
imagined.

**`/peek` on a group id answers that group's signature instead** — every inlet and outlet with its name,
type, branch and item counts, and a few values off each outlet:

```json
{ "ok": true, "group": "Steps",
  "inlets":  [ { "name": "Count",  "id": "…", "type": "Integer",      "count": 1, "branches": 1 } ],
  "outlets": [ { "name": "Result", "id": "…", "type": "Generic Data", "count": 1, "branches": 1,
                 "sample": ["55"] } ] }
```

A group is a function and this is its type as it stands, which is what you assert against after editing one.
Counts rather than full data, deliberately: six outlets of a thousand branches would flood the context
`/peek` exists to protect. Take a port's `id` and peek at that when you want the values.

Direction is derived, not stored: a port fed from outside the group is an inlet, one read from outside is an
outlet — the same rule the `/signature` verb uses when it plants them, so the two cannot disagree. Ports an
author placed by hand count too. A group with neither answers empty arrays and a `note` saying it has no
signature yet.

**Flags are read loosely.** `true`, `"true"` and `1` all mean true, because MCP clients routinely serialise
scalars as strings and a server that insisted otherwise would punish the wrong party.

**A refusal means the verb did not happen; a transport failure means you do not know.** That distinction is
the protocol's, not a convention, and the verbs are not idempotent — `wire` and `set` and `place` sent twice
make two of everything. So:

- A JSON body with `ok:false` is authoritative. Nothing was applied.
- `"Rhino is busy: the UI thread is working"` means the work never started. Safe to send again; ask `/pulse`
  first, since it answers while the thread is held and distinguishes a long solve from a modal.
- `"This started and has not finished after 5 minutes"` means the opposite. It is running and will finish.
  **Do not send it again.**
- No answer at all — a timeout, a dropped socket — tells you nothing about whether it ran. **Do not retry.**
  Read `/events` and look for an entry under your own `author`; every mutating verb journals one, so the
  journal is the record of what landed. One extra call, always correct.

Under the hood: work is queued onto Rhino's UI thread, and abandoning the wait cannot unqueue it. So the wait
is a handshake — pending work can be abandoned, started work is waited for — which is what makes the three
sentences above trustworthy rather than hopeful. Answers that cannot be delivered because the caller has gone
are counted and otherwise ignored; the verb ran, and there is nobody left to tell.

**Notes carry their text and their position.** A `Scribble` and a `Panel` both take `text` on `place` and can
be reworded with `/set`; empty or whitespace text is refused rather than silently replaced with a placeholder.
`/describe` answers `annotation: {kind, text, at, box, group, groupName}` and `/canvas` carries the same per
note, so wording *and* placement are checkable without a screenshot.

**A note's group decides where `/arrange` puts it.** Notes are laid out by a pass of their own, after the
components have their positions — they are not part of the layout algebra, having no ports and no dataflow, and
they do not need to be. The rule needs no new field: a note **in a group** is that group's caption and goes
above the group's other members; a note **in no group** is about the whole definition and goes above everything
as a title. `/place` already says which by taking a `group`, and `/describe` reports it back. Repeated
`/arrange` calls land on the same coordinates.

One consequence worth knowing, since `/review` reports it: a scribble is a single unwrapped line, so a caption
longer than the components it sits over makes the frame around its group wider than the room the layout
reserved, and two group frames can touch. `/arrange` will not clear that — it has already placed everything
where it means to. The finding says so, and says to shorten the note instead.

**`/preview` takes a group id or an object id, and `ids` for a list of either.** Three granularities from one
verb: no id sweeps the document on the colour rule — only the outlets of the red and yellow groups keep
drawing — a group id quiets that group on its own terms whatever colour it wears, and an object id quiets
exactly that object. `on:true` gives any of them back. The answer carries a `groups` array and an `objects`
array, each with what ended up drawing rather than what this call changed. Every id is checked before any
flag moves, so a bad id in a list refuses the whole list and says which id it was.

The object granularity was added because the group one could not reach the case that mattered: an
intermediate component flooding the viewport while the rest of its group has to keep drawing. Reported from a
facade of 960 panels interpolated through 24 points each — 23,040 preview markers over the building, and no
usable view of it. One verb rather than two, because what differs between a group and an object here is only
the policy over members, and only the sweep has a policy; a second verb would have meant every caller
knowing which of the two a given id wanted before it could ask.

Drawing is not a parameter, and `/set` with `param: "preview"` now says so and names this verb. It used to
refuse accurately and unhelpfully, which is how the author above concluded the thing could not be done at
all.

**An edit marks the document modified, so closing Rhino will offer to save it.** Since 0.22.0. Before that
the link changed a document and left `IsModified` false, so Rhino closed it without asking and the human
lost an agent's work with no prompt at all — an edit is an edit whoever made it. `/canvas` reports
`modified` and `path`, so a client can see the state rather than infer it, and `/save` clears the flag.

Reading never marks it, and neither does `/select` or `/zoom` — those are ways of looking. `/arrange`,
`/signature` and `/preview` mark only when they actually changed something, because all three are finishing
moves people run more than once and a save prompt for having run one twice teaches everybody to dismiss the
prompt unread.

**Verify numerically.** `/peek` returns branch and item counts with paths; that is the specification. A
screenshot tells you a definition looks plausible, which is not the same claim. `/canvas-image` and
`/screenshot` exist for the human's half of the pairing.

**But not for anything painted onto a control.** `/canvas-image` re-renders the document to a bitmap rather
than photographing the window, so overlays drawn during the canvas paint do not appear in it. Their only
witness is the screen itself.

## When nothing answers

Every verb above needs the UI thread, so when that thread is held they all time out together. The process
server exists to tell you which of two opposite situations you are in, and it never touches the UI thread
itself.

**`GET /pulse`** answers `idle`, `busy` or `blocked`. An idle handler stamps the time whenever the UI
thread has nothing to do, so a stale stamp means it is not free; the command events say what is running,
cached as they fire rather than asked for on demand; and Windows says whether a modal is up, because it
disables the owner window while one is open.

```json
{ "ok": true, "state": "blocked", "uiFree": false,
  "dialog": { "present": true, "title": "Explode Large Mesh",
              "buttons": ["Yes", "No", "Cancel"], "clickable": true },
  "advice": "The dialog \"Explode Large Mesh\" is open. Nothing will answer until somebody clicks it." }
```

Stale with a command running is *wait*. Stale with a dialog up is *stuck*, and the answer names it.

**`POST /dismiss`** answers that dialog: `{button}` presses one by name, `{key}` types instead, and neither
closes it. Closing is the default because closing is what the X does and what the X does is decline.
`{expect}` names the dialog you meant to answer and refuses if another is up by then — dialogs are
transient, and a blind press answers whatever happens to be there.

**`clickable: false` is not an empty list of buttons.** It means the dialog draws its own — Rhino's Eto
prompts do — so the buttons are not windows and there is nothing to post a click to. `WM_CLOSE` is no
substitute either: on a *save changes?* prompt, closing means cancel, so the thing you were trying to do
does not happen. Send a key.

**`POST /escape`** is the gap `/dismiss` leaves. A command waiting on a pick is not a dialog: nothing is
disabled, there is no window to enumerate, and `/dismiss` correctly refuses — yet the UI thread is held all
the same, so every other verb answers *busy* as though waiting would help. Scripting an interactive command
is the ordinary way to arrive there. `{times}` cancels that many levels; one by default, capped at five,
because a stream of Escapes into an idle Rhino clears a selection somebody wanted. The key is queued rather
than delivered, so ask `/pulse` afterwards instead of trusting the answer.

**A Rhino on its way out is the one case worth knowing about.** Closing a document with unsaved changes
stops on Grasshopper's multi-save prompt, and by then Rhino has already destroyed its own frame — so a
diagnosis that asks the operating system which window is the main one gets handed the prompt itself, sees an
enabled window, and reports *busy, working on something unnamed*. Since 0.22.0 the frame is remembered from
the first idle instead, a destroyed frame stays destroyed, and the prompt is named with its buttons listed.
`/dismiss {button:"Close"}` then ends the process cleanly. Before that it took Win32 by hand.

**`GET /console?tail=50`** is the tail of Rhino's own command line, which is where commands and scripts
reply and which used to go only to the human. It is drained when the UI thread breathes, so a long
script's output arrives in one piece when the script ends — `/pulse` is the verb for the meantime.

**There is one capture per Rhino, and the process server owns it.** `CapturedCommandWindowStrings` clears
the buffer as it reads, so two drains do not double the lines — they halve them, each taking whatever
instalment it reached first. The `.rhp` loads before any canvas exists and starts the drain; the canvas
server finds capture already on, does not start a second, and answers `/console` by reading the process
server over loopback. Where only the `.gha` is installed, it drains for itself as it always did.

**`?mine=true` answers the link's own lines instead.** The plugin's voice is filtered out of `/console` so an
agent does not read its own requests back as though Rhino had said them — which is right, and which also made
the bridge's own faults unreadable *through the bridge*, at exactly the moment they are wanted. They are kept
in a ring of their own and served on request.

### Working in a Rhino with no canvas

The process server also runs the two verbs that need no definition, so a Rhino started without Grasshopper
is still a session an agent can work in — open a file, select, run a command, export, read what Rhino said.

**`POST /command`** takes `{script}` and runs it as a Rhino command script; a leading `-` keeps the dialogs
away. **`GET /doc`** answers the document: its name, whether it is modified, the layers with their
visibility and locks, the object count, and where the camera is.

Both need the UI thread — they are commands, and commands run there — so unlike `/pulse` they time out
when it is held. The timeout says which situation you are in, borrowing pulse's sentence to say it.

The canvas server answers these two as well, at `GET` and `POST /rhino`, and will go on doing so. A client
that finds a `phenome-rhino-*.port` should prefer it: those verbs are there whether or not Grasshopper was
ever opened. A `404` from an older `.rhp` is the signal to fall back to the canvas server, not to give up —
one half of a pairing is often updated before the other.

### A Rhino nobody opened

The third server is a program rather than a plugin: it starts a Rhino core in its own process with
`WindowStyle.NoWindow` and answers about files on disk, since there is no document anybody is looking at. Same
conventions — loopback, one JSON out, an ephemeral port in `%TEMP%\phenome-rhinoinside-<pid>.port` — so the
same client code reaches it.

**`GET /doc?path=`** describes a `.3dm`: units, tolerance, layers, and a count of each kind of object rather
than a line per object. **`POST /convert`** takes `{from, to, version?}` and writes the format the target's
extension asks for — `.3dm` through the archive writer, anything else through Rhino's exporter for it.
**`GET /pulse`** says whether the core is free and which verb it is on, answered without the work queue so it
answers while the queue is busy. **`POST /quit`** ends the process.

**Rhino commands do not run there, and that is measured rather than assumed.** `RunScript` answers `false` and
changes nothing — through the serial-number overload against a headless document, and against one opened the
ordinary way, which in a windowless process is headless anyway. So anything that is a command is out of reach:
selection, export option dialogs, most of what a toolbar does. The process server inside a real Rhino is where
that belongs. What does work is reading, writing, and Rhino's importers and exporters, which do load in a
Rhino with no window — verified for `.stl`, `.obj`, `.dxf` and `.step`.

**An application with no window can still put up a window.** Asking for file version 7 on a document holding
Rhino 8 data raises a modal in a `NoWindow` core, with nobody there to press one of its three buttons; the
write returns false and the thread waits. Every write from that server therefore sets `SuppressDialogBoxes`
and `SuppressAllInput`. Worth knowing if you drive a headless Rhino yourself.

## What is deliberately not here

**No authentication.** The socket is loopback-only and the trust boundary is the machine. If that is not
your threat model, do not expose the port.

**No transactions.** Each verb is atomic in itself; a sequence of them is not. If a `/place` half-succeeds,
the journal says what landed and `/canvas` says what exists — reconcile against those, not against what you
expected.

**No schema version.** The protocol is described by `GET /` at run time. A client that reads it at startup
adapts; a client that hard-codes the verb list will find out the hard way.

**No composition rules.** How to build a *good* definition — groups as functions with signatures, four role
colours, data on wires and never as text — is a different subject and lives with whoever is building. The
VS Code extension plants those notes as `AGENTS.md` in the workspace it pairs with.
