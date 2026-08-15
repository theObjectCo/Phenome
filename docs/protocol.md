# The protocol, for whoever writes the next client

The MCP server in the extension is one client of a plain HTTP API. This describes that API's shape well
enough to write another — a script, a different editor, an agent harness that would rather not run Node.

**`GET /` is the authority, not this file.** It is generated from the server's own dispatch table, so it
lists every verb, its arguments and its answers, and it cannot drift from what the server does. What follows
is the part a generated description cannot tell you: the session model, the rules the verbs share, and the
half-dozen things that will otherwise cost you an afternoon.

## Finding a session

The plugin binds an **ephemeral loopback port** at startup and writes it to:

```
%TEMP%\phenome-link-<rhino pid>.port
```

That file is the whole of discovery. There is no registry, no daemon and no fixed port.

- **One file per Rhino.** Several can run at once, and each is a separate canvas with a separate journal.
  An agent that should stay on one canvas holds on to one port for the session.
- **A stale file has a dead pid.** Check the process before trusting the port; the plugin cannot always
  clean up after a crash.
- **No file means no session** — Rhino is not running, or Grasshopper was never opened, or the `.gha` did
  not load. On a fresh install the third is most likely, and the cause is almost always a blocked assembly
  (see the README's install notes).

Everything is `http://127.0.0.1:<port>`. The listener never binds anything but loopback. `Access-Control-Allow-Origin`
is `*`, which is safe for exactly that reason: the clients are local windows, and nothing off the machine
can reach the socket in the first place.

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

**Verify numerically.** `/peek` returns branch and item counts with paths; that is the specification. A
screenshot tells you a definition looks plausible, which is not the same claim. `/canvas-image` and
`/screenshot` exist for the human's half of the pairing.

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
