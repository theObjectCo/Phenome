# Every verb, and which half answers it

As of 0.31.0. Fifty-two tools over two HTTP servers and one client.

The MCP server is registered as `phenome`, so a host presents these as `mcp__phenome__<name>`. The prefix
comes from the registration key the pairing writes, not from the name the server reports in its handshake.

| half | born with | answers about |
|---|---|---|
| **RhinoLink** (`.rhp`) | Rhino | the process, the document, the viewport, the plug-ins |
| **GrasshopperLink** (`.gha`) | Grasshopper | the canvas: objects, wires, data, layout, definitions |
| **mcp.js** | the agent session | starting, choosing and restarting a Rhino |

The client asks the Rhino half first for anything that half owns, and falls back to the canvas half on a
404 — so a pairing where only one side has been updated keeps working. Verbs marked *(also on `.gha`)*
exist in both for that reason.

## RhinoLink — 11 verbs, and none of them need a canvas

Start with `launch grasshopper:false` for all of these.

| verb | endpoint | what it does |
|---|---|---|
| `pulse` | `GET /pulse` | idle, busy or blocked — answered off the UI thread, so it answers when nothing else does *(also on `.gha`)* |
| `dialog` | `POST /dialog` | answer the open dialog: `button` presses, `key` types, `close` declines. With no answer given it refuses and lists the buttons rather than deciding for you *(also on `.gha`)* |
| `dismiss` | `POST /dismiss` | superseded by `dialog`, kept working: same thing, except that sending nothing closes the dialog and so declines *(also on `.gha`)* |
| `escape` | `POST /escape` | cancel whatever Rhino is waiting for, for the case `dismiss` cannot answer *(also on `.gha`)* |
| `console` | `GET /console` | the tail of Rhino's command line, where commands and scripts reply. `mine:true` reads the link's own echo from the canvas half instead |
| `rhino_command` | `POST /command` | run a Rhino command script in the scripting dialect *(also on `.gha` as `/rhino`)* |
| `rhino_doc` | `GET /doc` | the Rhino document: name, layers, object count, camera *(also on `.gha` as `/rhino`)* |
| `plugins` | `GET /plugins` | every plug-in Rhino has a record of, loaded or **not**, with path, registry key, managed flag, load protection — and the runtime Rhino is hosting. Grasshopper's libraries are merged in when a canvas is open |
| `rhino_load` | `POST /load` | load a plug-in by id or path, quietly and again after a failure |
| `screenshot` | `GET /screenshot` | the active viewport as PNG, framed for the capture and the camera put back *(also on `.gha`)* |
| `camera` | `GET`/`POST /camera` | read or aim the active viewport; only what you pass changes *(also on `.gha`)* |

## GrasshopperLink — 38 verbs about the canvas

### Reading

| verb | endpoint | what it does |
|---|---|---|
| `canvas` | `GET /canvas` | the whole document, or `as:'mermaid'` for its shape at a fiftieth of the size |
| `canvas_image` | `GET /canvas-image` | the canvas as a picture, fitted to the document |
| `describe` | `GET /describe` | one object's parameters, types, access, wire and item counts; a note's text and box |
| `peek` | `GET /peek` | one parameter's full data with tree paths — or a group's whole signature |
| `wires` | `GET /wires` | every wire in the document, from and to |
| `review` | `GET /review` | the document against the composition rules |
| `components` | `GET /components` | search the installed catalogue by name or description |
| `scripts` | `GET /scripts` | the script components on the canvas, with their generation |
| `script_read` | `GET /script` | one script component's source |
| `events` | `GET /events` | the journal after entry N — what the canvas did and who did it |

### Building

| verb | endpoint | what it does |
|---|---|---|
| `place` | `POST /place` | a whole group body in one call: objects, local ids, wires, constants. Prefer this over add/wire loops |
| `add` | `POST /add` | one component or parameter by name or guid |
| `wire` | `POST /wire` | every wire in one call; `disconnect` takes one back |
| `set` | `POST /set` | every value in one call: slider domains, panel text, a constant into a socket |
| `param` | `POST /param` | data mapping on one parameter: flatten, graft, simplify, reverse |
| `group` | `POST /group` | a named group, declared signature-first with inlets and outlets |
| `ungroup` | `POST /ungroup` | dissolve a group, keeping its members |
| `signature` | `POST /signature` | give a group named ports at its edges and re-land the crossing wires |
| `arrange` | `POST /arrange` | lay the document out in layers; notes become captions. Idempotent |
| `select` | `POST /select` | select objects, replacing the selection unless `add` |
| `zoom` | `POST /zoom` | frame the canvas view on those objects |
| `delete` | `POST /delete` | remove objects; refuses when it would cut live wires |
| `undo` | `POST /undo` | one step back through Grasshopper's own stack |
| `redo` | `POST /redo` | one step forward |
| `script_write` | `POST /script` | new source into a script component, with its compile errors back |

### Documents

| verb | endpoint | what it does |
|---|---|---|
| `documents` | `GET`/`POST /documents` | every open document with its unsaved state and which one the canvas shows; `use` switches |
| `new_document` | `POST /new` | a fresh document on the canvas |
| `open` | `POST /open` | open a `.gh` on the canvas, or a `.3dm` in Rhino |
| `save` | `POST /save` | save where it lives, or to `path` |
| `close` | `POST /close` | close a document, discarding what is unsaved |
| `saveandclose` | `POST /saveandclose` | write it first, then close |

### Running

| verb | endpoint | what it does |
|---|---|---|
| `solver` | `POST /solver` | lock or unlock the solver |
| `bake` | `POST /bake` | bake those objects into the Rhino document |
| `preview` | `POST /preview` | quiet the preview: the whole document on the colour rule, or one group, or one object |

### Talking

| verb | endpoint | what it does |
|---|---|---|
| `say` | `POST /say` | a message into the journal, for whoever reads it |
| `report` | `POST /report` | leave a note where a verb fought you |
| `friction` | `GET /friction` | the friction log: refusals and reports, newest last |
| `feedback` | `POST /feedback` | assemble the whole complaint into one file and a mail draft. Ask the human first |

## mcp.js — 3 verbs with no endpoint

These are about the process rather than anything inside it, so no server can answer them: two of them
exist precisely when nothing is running.

| verb | what it does |
|---|---|
| `launch` | start Rhino and wait for the link. `fresh` starts a second one; `grasshopper:false` starts Rhino alone |
| `sessions` | every live session on the machine, canvas and Rhino; `use` pins one so later verbs mean it |
| `restart` | end this agent's Rhino and bring a fresh one up — the only way a rebuilt assembly reaches a running Rhino, since a .NET plug-in cannot be unloaded. Refuses while either half holds unsaved work |

## Two things the table does not show

**Hybrids.** `plugins` and `console` ask both halves: `plugins` takes the records from Rhino and the
libraries from the canvas, `console` takes Rhino's own line from Rhino and the link's echo from the canvas.
Each answers usefully when the other half is absent.

**Duplicates are deliberate.** Eight verbs live in both halves. The canvas copies came first and are kept
as the fallback, which is what lets one half be updated before the other.
