# Every verb, and which half answers it

The list is as of 0.35.0: fifty-four tools over two HTTP servers and one client.

The MCP server is registered as `phenome`, and a host presents these tools as `mcp__phenome__<name>`. The
prefix comes from the registration key the pairing writes, not from the name the server reports in its
handshake.

| half | born with | answers about |
|---|---|---|
| **RhinoLink** (`.rhp`) | Rhino | the process, the document, the viewport, the plug-ins |
| **GrasshopperLink** (`.gha`) | Grasshopper | the canvas: objects, wires, data, layout, definitions |
| **mcp.js** | the agent session | starting, choosing and restarting a Rhino |

The client asks the Rhino half first for anything that half owns, and falls back to the canvas half on a
404. A pairing where only one side has been updated keeps working this way, and verbs marked
*(also on `.gha`)* exist in both halves for that reason.

## RhinoLink: 11 verbs, none of which needs a canvas

Start with `launch grasshopper:false` for all of these.

| verb | endpoint | what it does |
|---|---|---|
| `pulse` | `GET /pulse` | idle, busy or blocked, answered off the UI thread and therefore available when nothing else answers. A blocking dialog comes with its title, its message as `text` and its buttons, Rhino's own Eto dialogs included *(also on `.gha`)* |
| `dialog` | `POST /dialog` | answer the open dialog: `button` presses (through UI Automation where the button has no window), `key` types a letter or a named key such as `{ESC}`, `close` declines. With no answer given it refuses and lists the buttons instead of deciding for the caller *(also on `.gha`)* |
| `dismiss` | `POST /dismiss` | superseded by `dialog` and still working. It behaves the same, except that sending nothing closes the dialog, which declines it *(also on `.gha`)* |
| `escape` | `POST /escape` | cancel whatever Rhino is waiting for, for the case `dismiss` cannot answer *(also on `.gha`)* |
| `console` | `GET /console` | the tail of Rhino's command line, where commands and scripts reply. `mine:true` reads the link's own echo from the canvas half instead |
| `rhino_command` | `POST /command` | run a Rhino command script in the scripting dialect *(also on `.gha` as `/rhino`)* |
| `python` | `POST /python` | run Python 3 and answer in the same call with stdout, stderr, error, traceback and `result`. The exception is caught, so Rhino's exception box never opens; `globals` sets variables and `layer` takes every object the script adds |
| `rhino_doc` | `GET /doc` | the Rhino document: name, layers, object count, camera *(also on `.gha` as `/rhino`)* |
| `plugins` | `GET /plugins` | every plug-in Rhino has a record of, loaded or **not**, with path, registry key, managed flag, load protection, and the runtime Rhino is hosting. Grasshopper's libraries are merged in when a canvas is open |
| `rhino_load` | `POST /load` | load a plug-in by id or path without its confirmation dialog, and again after a failed attempt |
| `screenshot` | `GET /screenshot` | the active viewport as PNG at the width and height asked for, kept on disk, framed for the capture and the camera put back *(also on `.gha`)* |
| `camera` | `GET`/`POST /camera` | read or aim the active viewport; only the fields passed change *(also on `.gha`)* |

## GrasshopperLink: 40 verbs about the canvas

### Reading

| verb | endpoint | what it does |
|---|---|---|
| `canvas` | `GET /canvas` | the whole document, or `as:'mermaid'` for its shape at a fiftieth of the size |
| `canvas_image` | `GET /canvas-image` | the canvas as a picture, fitted to the document, drawn at the size asked for and kept on disk |
| `describe` | `GET /describe` | one object's parameters, types, access, wire and item counts; a note's text and box |
| `peek` | `GET /peek` | one parameter's full data with tree paths, or a group's whole signature |
| `measure` | `GET /measure` | lengths, areas and volumes on one parameter; with `against`, overlaps and nearest distance between two sets |
| `wires` | `GET /wires` | every wire in the document, from and to |
| `review` | `GET /review` | the document against the composition rules |
| `components` | `GET /components` | search the installed catalogue by name or description |
| `scripts` | `GET /scripts` | the script components on the canvas, with their generation |
| `script_read` | `GET /script` | one script component's source |
| `events` | `GET /events` | the journal after entry N: what the canvas did and who did it |

### Building

| verb | endpoint | what it does |
|---|---|---|
| `place` | `POST /place` | a whole group body in one call: objects, local ids, wires, constants. Prefer this over add/wire loops |
| `add` | `POST /add` | one component or parameter by name or guid |
| `wire` | `POST /wire` | every wire in one call; `disconnect` takes one back |
| `set` | `POST /set` | every value in one call: slider domains, panel text, a constant or a list into a socket; also a parameter's name and a panel's size |
| `param` | `POST /param` | data mapping on one parameter: flatten, graft, simplify, reverse |
| `group` | `POST /group` | a named group, declared signature-first with inlets and outlets |
| `ungroup` | `POST /ungroup` | dissolve a group, keeping its members |
| `signature` | `POST /signature` | give a group named ports at its edges and re-land the crossing wires |
| `arrange` | `POST /arrange` | lay the document out in layers; notes become captions; the document's top-left corner goes to (20, 20). Idempotent |
| `select` | `POST /select` | select objects, replacing the selection unless `add` |
| `zoom` | `POST /zoom` | frame the canvas view on those objects |
| `delete` | `POST /delete` | remove objects; refuses when it would cut live wires |
| `undo` | `POST /undo` | one step back through Grasshopper's own stack |
| `redo` | `POST /redo` | one step forward |
| `script_write` | `POST /script` | new source into a script component, with its compile errors back |
| `pillscript` | `POST /pillscript` | a PillScript component through PillScript itself: its files, references, compile and solve. Needs PillScript 0.5.0 or later in the same Rhino |

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
| `say` | `POST /say` | a message into the journal |
| `report` | `POST /report` | leave a note about a verb that got in the way |
| `friction` | `GET /friction` | the friction log: refusals and reports, newest last |
| `feedback` | `POST /feedback` | assemble the whole complaint into one file and a mail draft. Ask the user first |

## mcp.js: 3 verbs with no endpoint

These verbs concern the process itself, and no server can answer them. Two of them must work precisely when
no Rhino is running.

| verb | what it does |
|---|---|
| `launch` | start Rhino and wait for the link. `fresh` starts a second one; `grasshopper:false` starts Rhino alone; `packageDirs` sets RHINO_PACKAGE_DIRS for a plug-in loading from its build folder, and `restart` keeps it |
| `sessions` | every live session on the machine, canvas and Rhino; `use` pins one so later verbs mean it |
| `restart` | end this agent's Rhino and bring a fresh one up. This is the only way a rebuilt assembly reaches a running Rhino, since a .NET plug-in cannot be unloaded. Refuses while either half holds unsaved work; with `discard:true` it also answers the new Rhino's autosave recovery question with Cancel |

Before the link of a Rhino that `launch` started is up, `pulse` and `dialog` answer from here: the dialog
holding the start is read and answered through UI Automation from outside the process, and `launch` names it
with its message and buttons when it gives up waiting.

## Two things the table does not show

**Hybrids.** `plugins` and `console` ask both halves: `plugins` takes the records from Rhino and the
libraries from the canvas, `console` takes Rhino's own line from Rhino and the link's echo from the canvas.
Each answers usefully when the other half is absent.

**Duplicates are deliberate.** Eight verbs live in both halves. The canvas copies came first and are kept
as the fallback, which is what lets one half be updated before the other.
