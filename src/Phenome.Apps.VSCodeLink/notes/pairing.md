## Pairing with Grasshopper (Phenome Link)

**Use the MCP tools.** The `phenome` server registers one tool per verb, and the session already lists them with
their arguments; they are not repeated here. That list does not cover four habits. Search `components` before
`add` when a name is uncertain. Prefer `place` over add/wire loops. Verify with `peek` rather than `screenshot`,
because the canvas exposes positions directly. Use `launch` when there is no session, instead of starting Rhino by
hand.

**A missing `phenome` tool usually means one of three cases, each with its own answer.** In Claude Code the tools
may be listed by name only, as deferred tools: the server is present, and one `ToolSearch` with
`select:mcp__phenome__canvas,mcp__phenome__place,...` loads them. Tools that were in this conversation earlier and
are gone now mean the server dropped. It does not reconnect by itself: ask the user to reconnect it (`/mcp` in
Claude Code), then continue with the tools. One session lost them overnight, moved to HTTP the next morning, and
stayed on HTTP for a week after the tools returned. Only a conversation that never had a `phenome` tool has no
server configured. Restarting does not add one; *Without the MCP tools* at the end of this file describes the same
protocol over plain HTTP, including how to start a session with no `launch` verb. A narrower case sits outside
those three: most tools are present and one is missing, and restarting the session fixes it.

**Common components, with their guids and exact input names.** The table saves a search. Pass the **guid**, not
the name; the paragraph after the table gives the reason. For anything else, call `components` once and take the
guid from the answer. `describe` reports the real parameters of a placed object.

| what | component | guid | inputs |
| --- | --- | --- | --- |
| a knob | `Number Slider` | `57da07bd-ecab-415d-9d86-af36d7073abc` | (set its domain with `set`) |
| count out a series | `Series` | `e64c5fb1-845c-4ab1-8911-5f338516ba67` | Start, Step, Count |
| divide a span | `Range` | `9445ca40-cc73-4861-a455-146308676855` | Domain, Steps |
| add | `Addition` | `a0d62394-a118-422d-abb3-6af115c75b25` | A, B |
| subtract | `Subtraction` | `9c007a04-d0d9-48e4-9da3-9ba142bc4d46` | A, B |
| multiply | `Multiplication` | `ce46b74e-00c9-43c4-805a-193b69ea4a11` | A, B |
| divide | `Division` | `9c85271f-89fa-4e9f-9f4a-d75802120ccc` | A, B |
| a point | `Construct Point` | `3581f42a-9592-4549-bd6b-1c0fc39d067b` | X coordinate, Y coordinate, Z coordinate |
| take a point apart | `Deconstruct` | `9abae6b7-fa1d-448c-9209-4a8155345841` | Point → X component, Y component, Z component |
| a line | `Line` | `4c4e56eb-2f04-43f9-95a3-cc46a14f495a` | Start Point, End Point |
| a line from a direction | `Line SDL` | `4c619bc9-39fd-4717-82a6-1e07ea237bbe` | Start, Direction, Length |
| a box | `Box 2Pt` | `2a43ef96-8f87-4892-8b94-237a47e8d3cf` | Point A, Point B, Plane |
| a box about a centre | `Center Box` | `28061aae-04fb-4cb5-ac45-16f3b66bc0a4` | Base, X, Y, Z |
| gather lists into one | `Merge` | `3cadddef-1e2b-4c09-9390-0e8f78f7609f` | Data 1, Data 2, … (zoom adds more) |
| move something | `Move` | `e9eb1dcf-92f6-4d4d-84ae-96222d60f56b` | Geometry, Motion |
| the world X vector | `Unit X` | `79f9fbb3-8f1d-4d9a-88a9-f7961b1012cd` | Factor |
| the world Y vector | `Unit Y` | `d3d195ea-2d59-4ffa-90b1-8b7ff3369f69` | Factor |
| the world Z vector | `Unit Z` | `9103c240-a6a9-4223-9b42-dbd19bf38e2b` | Factor |
| flatten a tree | `Flatten Tree` | `f80cfe18-9510-4b89-8301-8e58faf423bb` | Tree |
| graft a tree | `Graft Tree` | `87e1d9ef-088b-4d30-9dda-8a7448a17329` | Tree |
| pair every A with every B | `Cross Reference` | `36947590-f0cb-4807-a8f9-9c90c9b20621` | List (A), List (B) |
| colour the preview | `Custom Preview` | `537b0419-bbc2-4ff4-bf08-afe526367b2c` | Geometry, Material |
| a colour | `Colour Swatch` | `9c53bac0-ba66-40bd-8154-ce9829b9db1a` | (set its value) |
| a note on the canvas | `Panel` | `59e0b89a-e487-49f8-bab8-b5bab16be14c` | (set its text) |
| a heading on the canvas | `Scribble` | `7f5c6c55-f846-4a08-9c9a-cfdc285cc6fe` | (set its text) |

Three rows are in the table because of a name collision, which the guid resolves. `Addition` is also a vector
component taking Vector A and Vector B. `Line` is also a *parameter* holding a collection of line segments.
`Merge` has a twin in the same category (`Sets › Tree` twice) taking Stream A and Stream B instead of Data 1 and
Data 2. `place` refuses each of the three by name and accepts each by guid.

**Name a component by its guid.** The `guid` column is `ComponentGuid`, the only identity Grasshopper guarantees.
A `.gh` file stores it to find the component again, and changing it would break every file that used it. Other
identifiers drift: a plugin author can rename a display name between releases, a nickname is editable per instance
on the canvas, and ribbon categories are reorganised across releases. Names also *collide*: with plugins
installed, `Addition`, `Merge`, `Scale`, `Rotate`, `Area` and `Deconstruct Domain²` each name more than one
component, and `place` refuses an ambiguous name instead of guessing. A wrong choice is not reported when it is
made. A vector `Addition` placed where the arithmetic one was meant surfaces three groups later as "Data
conversion failed from Text to Number" and is expensive to trace. This paragraph was written after six such
refusals in one definition, and passing the guid rules out both the refusal and the wrong choice. For anything not
in the table, call `components` once and use the guid it returns.

**A `place` recipe is all-or-nothing, and the refusal reports everything at once.** If one entry cannot be
resolved, nothing is placed and the canvas is unchanged; a failure never leaves orphaned components to clean up.
The refusal names **every** unresolved entry by the local id given in the recipe, and an ambiguous name comes back
as a paste-ready literal such as `{"name":"Merge","guid":"3cadddef-..."}`. Send the recipe, and if it is refused,
fix every entry it named and send the whole recipe again. Do not probe one entry at a time.

**Notes, and how to verify the wording.** `Scribble` and `Panel` both take their wording in `text` on `place`, and
`set` rewords either one afterwards. That is the repair path when the first wording was wrong; a note never needs
to be deleted and rebuilt.

```
place {objects:[{id:"note", name:"Scribble", text:"Tower shell - loft of the floor profiles"}]}
set   {id:"<that id>", value:"Tower shell - lofted from the floor profiles"}
describe {id:"<that id>"}
  -> {annotation:{kind:"scribble", text:"...", at:[x,y], box:[x,y,w,h], group:"...", groupName:"..."}}
```

**Read it back.** `describe` returns an annotation's text, its position, the rectangle it covers and the group it
belongs to. Call it after writing a note: it is the only way to confirm the wording landed. Until recently that
was not possible at all, and an agent had to wait for a user to look at the screen and report the error. Empty or
whitespace `text` is refused on create and on edit alike, and no placeholder is put in its place.

**A note's group is what the note is about, and that is the whole placement rule.** `arrange` places notes too, in
a pass after the components are positioned, and needs nothing but the group:

- **In a group** → the note is that group's caption and is placed above the group's other members.
- **In no group** → the note is about the whole definition and is placed above everything as a title.

Pass `group` on `place` when the note explains one function, and leave it out when it explains the whole
definition. Never position a note by hand. Running `arrange` twice gives the same coordinates twice, and it can
be called at any time without positions drifting.

**Do not say the same thing twice.** A scribble is a *comment*: the reason, the caveat, something a reader could
not infer. A group's nickname is the *function signature*: what the group does. A caption "Sphere radius" over a
group named "Sphere radius" costs the reader a line and adds nothing; write "SubD would need a different
exporter", or write no note. Most groups need no caption because the group name is their documentation, and that
is the reason to name groups well.

`review` reports one consequence of this. A scribble is a single unwrapped line. A long caption makes the frame
around its group wider than the components inside it, and two group frames can end up touching. `arrange` does not
fix that, because it has already placed everything; shorten the note.

**How to build.** Declare the structure first, the way code is written, instead of building a mess and tidying it:

1. **Write the plan as a mermaid flowchart** before touching the canvas: one `subgraph` per group, named for the
   one thing it does, with the values flowing between them. That diagram *is* the group structure. Step 2
   transcribes it without inventing anything, and the user can read the plan before a single component exists.
   Keep the diagram in the chat and off the canvas. To read a definition built elsewhere, ask for the same shape
   back: `canvas` with `as:'mermaid'`.
2. **Declare every group with its signature**: one `group` call per step, with `inlets` and `outlets` named after
   the plan. The answer is a name-to-id map of ports, and the skeleton of the definition exists before a single
   component does.
3. **Fill each body**, one group at a time. `place` the components, wiring them onto that group's inlet ids and
   into its outlet ids in the same call, then send one batched `wire` for the connections between groups. Those
   run outlet to inlet, never component to component across a boundary.
4. **`arrange`, then `review`, then fix, then `save`.** Nothing is positioned by hand at any point and nothing is
   left unsaved. An unsaved canvas exists only inside a running process.

**When a call fails at the transport layer, do not resend it.** The rule protects work that may already be in the
canvas, including another agent's or the user's. A timeout, a dropped connection or "the specified network name is
no longer available" does not say whether the verb ran. `wire`, `set` and `place` are not idempotent: sending one
twice creates two of everything.

Instead, read `/events` (the `events` tool) and look for an entry under **the author name this agent sends**.
Every mutating verb journals one, and the journal is the record of what actually landed. If the entry is there,
the call worked: continue. If it is not there, resend. The check costs one call and is always correct, where a
blind retry can be destructive.

Two answers are exact, and they mean opposite things:

- **"Rhino is busy: the UI thread is working"** means the verb *never started*. It is safe to retry, and better
  after a short wait than immediately. Ask `pulse`: it answers while the thread is held and reports whether Rhino
  is working on something or waiting on a dialog, two states that need opposite responses.
- **"This started and has not finished after 5 minutes"** means the verb is running and will finish. Do not resend
  it; read `/events` to see when it lands.

**A slow answer is usually a queue.** Every verb that touches the document runs on Rhino's single UI thread, one
at a time. While the user drags a slider, or another agent is part way through a `place`, a call waits behind it,
and that is normal. `pulse` reports the state. The correct response to a queue is to wait, and a second copy of
the request is the wrong one.

**Edits made through the link set the modified flag** exactly as a user's do: the Grasshopper title carries an
asterisk and closing Rhino offers to save. That prompt is a safety net and does not replace step 4. Work left for
the user to notice at closing time gives them a prompt about a definition they did not write, and they have to
guess what was in it. Saving when the work is done means the prompt never appears. `canvas` reports `modified` and
`path`; read the state instead of assuming it.

**Settle grouping and naming before `signature`.** Do all grouping and naming *first*. To rename or recolour a
group, call `group` again with its `id`; never ungroup and regroup. Only then call `signature` once, followed by
`arrange` and `review`. Repeated `signature` calls between regroupings produced parallel chains sharing endpoints,
where disconnecting a wire appeared to do nothing and a later delete removed the live copy. `signature` is
idempotent now and is still a finishing step. Keep group names to plain characters: write "and" instead of "&",
and never an HTML entity.

**When the structure looks wrong, inspect before deleting.** `wires` returns the full connection list and `peek`
returns one parameter's data. `delete` refuses when it would cut wires to objects that stay, and it names those
wires; read that list instead of forcing the delete. `undo` steps back one operation, and a delete or an arrange
can be reversed with it.

**Composition: the rules `review` enforces.** A canvas is read group by group, and the groups ARE its abstraction
layer.

1. **A group is a function and does exactly one thing.** Single responsibility applies literally. A name that
   needs an "and", a comma or a slash belongs to two groups: "halve the dimensions" and "compute the leg height"
   are two functions, and one group called "halves and heights" is wrong. Name every group for the one thing it
   does. A group of moderate size holds up to about thirty objects; split a larger one.
2. **A group is a virtual component and gets a signature.** Call `signature` once, after all grouping is settled.
   It plants named floating parameters just inside the left edge as inlets and at the right edge as outlets, and
   re-lands every crossing wire on them. Nothing crosses a boundary except through them. Wiring one group's
   component straight into another's bypasses the interface.
3. **Never rename a component.** A component's nickname is how everyone recognises it. Renaming Multiplication to
   "W/2" makes the canvas unreadable for the next reader, the agent that renamed it included. Names belong on the
   floating parameters, and only parameters get nicknames.
4. **Never position groups by hand; call `arrange`.** It lays groups out as whole blocks, which is the only way
   frames stay apart. A layout that places components one by one interleaves the members of different groups, and
   interleaved members force their frames to overlap. Nest one level at most. `group` and `arrange` keep a mother
   group at the very back of the draw order.
5. **Colour by role.** There are four roles and no others. Colours are drawn at quarter opacity; give the full
   colour. **blue** `[70,110,255]` marks inputs the user may modify: the sliders themselves, and nothing more. Do
   not duplicate a parameter to have a second copy of it lying around, because a copy that feeds nothing is one
   more thing the next reader has to check before ignoring it. Do **not** collect every input into one bank
   either. A blue group belongs where its knobs are used, beside the function they feed, and a reader then finds
   each knob next to its effect. **red** `[255,60,60]` marks the components whose geometry gets baked into Rhino
   as the product; selecting the group and baking it gives everything needed. **yellow** `[255,220,0]` marks
   preview-only geometry, never baked. **grey** `[150,150,150]` marks a plain function. Keep the flow left to
   right.
6. **Sliders get real domains.** `set` takes `minimum`/`maximum`/`decimals`, or a string value like
   `"0<1400<2400"` that sets the whole domain at once. A bare 0..1 slider is almost always wrong. A constant goes
   in the socket that uses it (`set` with `param`, with no parameter and no wire) unless a reader needs to see it.
   A value typed into a socket is invisible on the canvas. Anything a reader would look for while reading the
   definition, such as a dimension, a tolerance or a name, belongs in a `Panel` wired in, where it can be seen and
   changed without opening anything. Put one value in each panel: a panel is still a wire and does not carry
   several things packed as text.
7. **Respect the data tree, and do not flatten a way out of trouble.** Grasshopper data is a tree of branches with
   paths like `{0;1}`. A component runs once per item in the *longest* input and reuses the last item of the
   shorter ones. A stray extra item raises no error and silently multiplies the geometry.
   - **Keep paths clean and meaningful.** A path says where a thing belongs (which desk, which leg), and paths
     match between trees meant to pair. If two trees will not pair, fix the structure that made them instead of
     papering over it.
   - **Flatten and graft belong on the canvas as components**, where a reader sees the structure change. As
     modifiers on a parameter they are hidden. Never reach for flatten to make a mismatch go away: it discards
     exactly the information the paths were carrying.
   - **Two wires into one socket meet only where their paths agree.** Grasshopper concatenates by path, and
     sources at different depths (one on `{0}`, one on `{0;0}`) never land in the same branch. The component runs
     once per branch on half the data each time, and nothing turns red. A `Boundary Surfaces` handed an outline on
     `{0}` and its offset on `{0;0}` returns two separate surfaces instead of one with a hole in it, and it looks
     correct until the geometry is measured. After wiring several sources into one socket, `peek` that *input*,
     not the outputs that feed it. `review` reports this as "mismatched paths" and marks it blocking.
   - **Never use the simplify modifier.** It silently drops path components depending on the shape of the tree at
     that moment, and the same definition then behaves differently on different data. The defect appears in
     another file, months later. If a structure needs changing, change it visibly, with a component.
   - **Build for many, even when asked for one.** If the brief says one desk, make the definition work for a list
     of desks: one branch per desk, with the structure preserved end to end. Done from the start this costs
     nothing; added later it is a rewrite.
   - **Data travels on wires, one value per wire, and never as text.** Do not use Format or Concatenate to pack
     several numbers into a string and feed that to a numeric input. It fails as "Data conversion failed from Text
     to Number", and even when a conversion succeeds the structure is gone. Use Merge to gather several values
     into one list. To pair values, wire them into separate inputs.
   - **The pattern for making many of something.** Take N units, each with M shelves. Put the N per-unit values
     into N branches (`Merge` the sliders, then `Graft Tree` for one branch per unit), and per-unit arithmetic
     then broadcasts by itself. Plain matching will not cross two differently shaped trees. Where a list that is
     the *same for every unit* (the M shelf heights, the rung positions) has to meet the per-unit branches, use
     **`Cross Reference`**: it pairs every A with every B and gives M items inside each unit's branch. Getting
     this wrong is what silently produces 1806 of something.
   - **Verify with `peek`** after each group: branch count and item counts are the specification. `review` counts
     how many times each component runs and compares that with the items its inputs hold. More runs than the
     largest input has items is exactly this failure, and it is blocking above 100 runs in one branch. `review`
     also reports every red or orange component, and a review with nothing left to report means the definition
     actually runs.
8. **Build with components, and use a script only as the last resort.** A definition made of components is
   readable and editable for the next person to open it. Reach for a C# script component only when no combination
   of components can do the job, and say why when doing so.
9. **Leave no dead ends.** Every component and parameter either feeds something or draws something. A parameter
   left over from a rethink, or a component whose output went elsewhere, is one more thing the next reader must
   check before ignoring it. `review` lists them as "unused"; delete them.
10. **When a tool fails, report it.** Call `report` with what was expected against what happened. Refused requests
    are logged automatically, and `report` is for the rest. If the user hits repeated trouble, **ask them**
    whether to prepare a report they can mail (`feedback` assembles it and returns a mailto). Never send anything
    on the user's behalf, and never call `feedback` without asking first.
11. **Finish with `arrange`, then `review`, and fix what review reports.** `review` measures what can be measured:
    overlapping frames, unnamed groups, names indicating two jobs, oversized groups, renamed components, bare
    boundary crossings and ungrouped objects. A definition checked this way converges instead of being assumed
    correct. Leave notes in panels where a reader will need them.
12. **Then quiet the preview, and only then save.** A definition that is built and checked previews everything it
    ever computed: the cutting boxes a difference already consumed, the construction curves, the profile that was
    extruded away. The user is left picking the product out of the construction geometry, or reads the
    construction geometry as the answer. The colours already state which geometry was meant to be seen, and
    **`preview` with no id** sweeps the document on exactly that rule. Only the outlets of the **red** groups
    (baked as the product) and the **yellow** ones (there to be looked at) keep drawing. Everything else stops:
    grey functions, blue knobs, every intermediate inside every group. Naming a group quiets that one group and
    keeps its outlets drawing, whatever its colour, and `on:true` gives a group its whole preview back for another
    look inside.

**Do not wait until step 12 if the viewport is already unusable.** `preview` takes a single object's id as readily
as a group's, and `ids` takes a list of either. When an intermediate output floods the view with a list of points
or a field of construction lines, quiet that component and continue. A facade of 960 panels interpolated through
24 points each put **23,040 point markers** over the building, and neither the user nor any screenshot could see
the product underneath. A preview flag was looked for on `set` and on `param` and not found, and the result was
assumed impossible, while the `preview` verb already did it. Drawing is controlled by that verb and by no
parameter.

## Without the MCP tools

Everything above is the same protocol either way; only the access method changes. Read this section when the host
has no `phenome` tools, and stop reading it as soon as it does. When the tools appear later in the conversation,
switch back to them, even with a script already written.

**A loop may run as a script, even with the tools at hand.** Trying 64 variants of an input and measuring each one
takes a few hundred calls, and one script looping over `set`, `peek` and `measure` is quicker to write and to read
back. Building and changing the definition goes through the tools: groups, `place`, `wire`, `signature`,
`arrange`, `review`, `preview` and `save`, which are the steps of the workflow above. Sizes and overlaps come from
`measure`. `set` renames a parameter with `nickname` and sizes a panel with `width` and `height`. When the loop
needs something no verb does, call `report`. A throwaway C# or Python script that edits the canvas hides the gap,
and two such scripts in one session left Rhino stuck in a modal exception dialog.

**Starting a session cannot be guessed, and the exact steps are below.** There is no verb for it. The server runs
*inside* Grasshopper and nothing can answer until Grasshopper is running, which is why `launch` is in the MCP
layer and not in the protocol. Two mistakes are common, and both leave a Rhino that never answers:

```powershell
# 1. Note which sessions exist BEFORE you start anything.
Get-ChildItem "$env:TEMP\phenome-link-*.port"

# 2. Start Rhino AND Grasshopper. Rhino alone is not enough - the plugin loads with Grasshopper,
#    so no Grasshopper means no canvas link, ever. The quotes go around _Grasshopper and NOT around
#    the whole argument. Measured, three ways, on Rhino 8:
#
#      /runscript="_Grasshopper"     Grasshopper opens, port file in ~6 s
#      "/runscript=_Grasshopper"     Rhino starts, no canvas, no port file, ever
#      /runscript=_Grasshopper       the same nothing
#
#    The last two leave you waiting for a file that is never written, and the wait is indistinguishable
#    from Rhino being slow. This is the command the launch tool runs.
& "C:\Program Files\Rhino 8\System\Rhino.exe" /nosplash '/runscript="_Grasshopper"'

# 3. Wait for a port file that was NOT in the list from step 1. Rhino takes its time - poll every
#    3 seconds, give it up to 90. Attaching to a port that was already there puts you on an already-running
#    Rhino's canvas, which is the one failure worse than not starting at all.
# 4. GET http://127.0.0.1:<that port>/ describes every verb, its arguments and its answers.
```

On a Mac, which is untried so far, the same steps differ in two places. The port files are in `$TMPDIR`, and Rhino
starts with `"/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros" -nosplash -runscript=_Grasshopper &`, with a
dash and no quotes round the script, because Rhino for Mac reads the Windows spelling as a file to open. A
`report` saying what worked is welcome.

A `phenome-rhino-<pid>.port` appears too, on its own port. That is the Rhino half, and it answers about the
process instead of the canvas: `GET /pulse` reports whether Rhino is idle, busy or blocked, and it works even
while the UI thread is held. That file alone, with no `phenome-link-<pid>.port`, means step 2's script did not run
and Grasshopper never opened. Recognise that failure; waiting longer does not fix it.

If starting a process is not possible, or step 2 keeps giving a Rhino with no canvas, **ask the user to open Rhino
and Grasshopper and to say when it is up.** The request takes one sentence and a few seconds and cannot go wrong,
while a second and third attempt leave Rhinos open in the background.

The port file holds nothing but the number. There is one file per Rhino. A stale one names a dead pid, and a port
that does not answer is a leftover and not a fault.

**The rules that apply whichever way the link is reached.** Put the agent's own name in `author` on every POST.
The journal records it, and that is how an agent skips its own echo and how two agents are distinguished. Every
verb in this file is an endpoint of the same name: `POST /place`, `POST /wire`, `GET /peek?id=`, `GET /review`,
`POST /arrange`, `POST /preview`, `POST /save`. The rest (the journal's cursor, its gaps, what each verb answers)
is in `GET /` and, at length, in the plugin's `docs/protocol.md`.
