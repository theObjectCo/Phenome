# Phenome

Public home for Phenome's open components. Two of them are here now — the pair that puts an agent and a
human on the same Grasshopper canvas. More will follow, the geometry kernel among them.

| | |
|---|---|
| [`src/Phenome.Apps.GrasshopperLink`](src/Phenome.Apps.GrasshopperLink) | The canvas end: a Grasshopper plugin that exposes the live document over loopback HTTP. |
| [`src/Phenome.Apps.VSCodeLink`](src/Phenome.Apps.VSCodeLink) | The editor end: a VS Code extension and an MCP server, so an agent speaks the protocol as tools rather than raw HTTP. |

## What the link is for

A Grasshopper definition is hard to work on together, because one person has the canvas and everyone else
has a description of it. The link removes the description: the document, its wires, its data and its
solver state are readable over HTTP, and the same verbs that read it can edit it. The window and the agent
end up peers — both are clients, neither owns the session.

It carries no Phenome dependency on purpose. The protocol is useful to any Grasshopper user with any
agent, and a self-contained `.gha` is what lets it ship on its own.

- **Look** — the document as JSON or as a mermaid flowchart, one object's real parameters, every wire, a
  parameter's full data with tree paths, the installed component catalogue, a linter, the canvas as a
  picture, the Rhino viewport.
- **Build** — place a whole group body in one call, wire and set in batches, group, plant a group's
  signature as floating parameters, lay the graph out, delete (it refuses when that would cut live wires),
  select, zoom, undo and redo.
- **Run and keep** — the solver, bake, data mapping, new/open/save, a C# component's source with its
  compile errors back.
- **Say** — messages both ways, and a friction log for when a verb fights you.

Everything that happens is appended to a journal with an author on every entry, so a client polls for what
changed and skips its own echo. `GET /` describes the whole protocol and is generated from the server
itself, which makes it the authority rather than this file.

## Installing

No release is published yet — build it from source, which needs the .NET SDK, Rhino 8 and Node.js.

```powershell
pwsh tools/build.ps1
```

That leaves both halves in `dist/`. Then:

1. Copy `Phenome.Apps.GrasshopperLink.gha` into `%APPDATA%\Grasshopper\Libraries\`, right-click it,
   Properties, and **Unblock** — Windows blocks assemblies that arrived from elsewhere, and Grasshopper
   will not load a blocked one.
2. Install the extension: `code --install-extension dist\phenome-link-<version>.vsix`. The canvas's
   **Pair with VS Code** button does this for you if you skip it.
3. Restart Rhino and open Grasshopper. The plugin picks an ephemeral port and writes it to
   `%TEMP%\phenome-link-<pid>.port` — one file per Rhino, so several sessions can run at once and each
   agent can have a canvas of its own.

## Talking to it

Any HTTP client is a peer:

```
curl http://127.0.0.1:<port>/            # the protocol, in full
curl http://127.0.0.1:<port>/canvas      # the document
```

From an agent, the MCP server is the better door: it wraps every verb as a named tool, so a session asks
for permission once per verb instead of once per call. Point your client at `mcp.js` in the extension, or
let the extension launch the agent for you — it pins the session to one canvas through an environment
variable.

## Licence

MIT. See [LICENSE](LICENSE).
