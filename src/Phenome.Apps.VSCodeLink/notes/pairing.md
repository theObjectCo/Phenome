## Pairing with Grasshopper (Phenome Link)

**Prefer the MCP tools.** The `grasshopper` server registers one per verb and your session already
lists them with their arguments, so they are not repeated here. Four habits that list cannot teach you:
search `components` before `add` when unsure of a name; prefer `place` over add/wire loops; verify
with `peek`, not `screenshot`, since the canvas carries positions and needs no picture; and use
`launch` when there is no session rather than starting Rhino yourself.

**If you cannot see any `grasshopper` tool at all, you are not on a stale session - your host has no MCP
server wired up.** Restarting will not conjure one. Do not spend another thought on it: go straight to
*Without the MCP tools* at the end of this file, which is the whole protocol over plain HTTP and includes
the one thing you cannot otherwise work out - how to start a session when there is no `launch` verb to call.
(If *some* tools are there and one you want is missing, that is the stale case, and restarting the session
does fix it.)

**The components you will reach for, with their guids and their exact input names** - so you need not search
for them. Pass the **guid**, not the name; the paragraph under the table says why, and it is not a style
preference. Anything unusual: ask `components` once and take the guid from the answer, and `describe` tells
you a placed object's real parameters.

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

Three of those rows exist because of a collision, and the guid is what settles it: `Addition` is also a
vector component taking Vector A and Vector B, `Line` is also a *parameter* holding a collection of line
segments, and `Merge` has a twin in the very same category - `Sets › Tree` twice - taking Stream A and
Stream B instead of Data 1 and Data 2. Named, any of the three is a refusal. By guid, none of them is.

**Name a component by its guid, not by its name.** The `guid` column above is `ComponentGuid`, and it is
the only identity Grasshopper guarantees: it is what a `.gh` file stores in order to find a component again,
so it cannot change without breaking every file that used it. Everything else drifts. A display name can be
renamed by a plugin author between releases, a nickname is editable per instance on the canvas, and ribbon
categories get reorganised whenever somebody feels like it. Names also *collide*: with plugins installed,
`Addition`, `Merge`, `Scale`, `Rotate`, `Area` and `Deconstruct Domain²` all name more than one component,
and `place` refuses an ambiguous name rather than guessing - a quietly chosen vector `Addition` instead of
the maths one surfaces three groups later as "Data conversion failed from Text to Number" and is miserable
to trace. Six of those refusals in one definition is what prompted this paragraph. Pass the guid and none of
it can happen. For anything not in the table, ask `components` once and use the guid it gives you.

**A recipe is all-or-nothing, and the refusal tells you everything at once.** If one entry of a `place` call
cannot be resolved, nothing is placed and the canvas is untouched - so a failure never leaves you a pile of
orphans to clean up. The refusal names **every** unresolved entry by *your own local id*, not just the first
one, and an ambiguous name comes back as paste-ready literals like `{"name":"Merge","guid":"3cadddef-..."}`.
So the loop is: send the recipe, and if it is refused, fix every entry it named and send the whole thing
again. Do not probe one entry at a time.

**Notes, and how to be sure you wrote what you meant.** Both take their wording in `text` on `place`, and
both can be reworded afterwards with `set` - which is the repair path when the first wording was wrong, so
you never have to delete and rebuild a note.

```
place {objects:[{id:"note", name:"Scribble", text:"Tower shell - loft of the floor profiles"}]}
set   {id:"<that id>", value:"Tower shell - lofted from the floor profiles"}
describe {id:"<that id>"}
  -> {annotation:{kind:"scribble", text:"...", at:[x,y], box:[x,y,w,h], group:"...", groupName:"..."}}
```

**Read it back.** `describe` returns an annotation's text, where it sits, the rectangle it covers and the
group it belongs to. Do that after writing one: it is the only way to know your wording landed, and until
recently it was not possible - an agent had to wait for a human to look at the screen and say "that is
wrong". Empty or whitespace text is refused rather than quietly turned into a placeholder, on create and on
edit alike.

**A note's group is what the note is about, and that is the whole placement rule.** `arrange` now places
notes too, in a pass after the components have their positions, and it needs nothing from you but the group:

- **In a group** → the note becomes that group's caption and is put above the group's other members.
- **In no group** → the note is about the whole definition, and is put above everything as a title.

So pass `group` on `place` when the note explains one function, and leave it out when it explains the piece.
Then never position it yourself. Running `arrange` twice gives the same coordinates twice, so you can call it
whenever you like without anything creeping.

**Do not say the same thing twice.** A scribble is a *comment* - the why, the caveat, the thing the next
reader would otherwise have to work out. A group's nickname is the *function signature* - what this does.
A caption reading "Sphere radius" over a group named "Sphere radius" costs a reader a line and tells them
nothing; write "SubD would need a different exporter" instead, or write no note at all. Most groups need no
caption: the name is the documentation, and that is the point of naming it well.

One thing to watch, since `review` will tell you about it: a scribble is one long line and cannot wrap, so a
wordy caption makes the frame around its group wider than the components inside it, and two group frames can
end up touching. `arrange` will not fix that - it has already put everything where it means to. Shorten the
note.

**How to build.** Not by making a mess and tidying it - by declaring the shape first, the way code is
written:

1. **Draw the plan as a mermaid flowchart** before touching the canvas - one `subgraph` per group, named
   for the one thing it does, with the values flowing between them. That diagram *is* the group structure,
   so step 2 is transcribing it rather than inventing it, and the human can read your plan before a single
   component exists. Keep it in the chat, not on the canvas. And to read a definition you did not build,
   ask for the same shape back: `canvas` with `as:'mermaid'`.
2. **Declare every group with its signature** - one `group` call per step, with `inlets` and
   `outlets` named after the pseudocode. You get back a name-to-id map of ports, and the whole skeleton
   of the definition exists before a single component does.
3. **Fill each body**, one group at a time: `place` the components (wiring them onto that group's inlet
   ids and into its outlet ids in the same call), then one batched `wire` for the connections between
   groups - outlet to inlet, never component to component across a boundary.
4. **`arrange`, then `review`, then fix, then `save`.** Nothing is positioned by hand at any point, and
   nothing is left unsaved: when you have finished editing, save the definition. An unsaved canvas is a
   session's work resting on a running process.

**When a call fails on the transport, do not send it again.** This is the one rule here that protects
somebody else's work rather than your own tidiness. A timeout, a dropped connection, "the specified network
name is no longer available" - none of those tell you whether the verb ran. `wire` and `set` and `place` are
not idempotent: sending one twice makes two of everything.

What to do instead: read `/events` (the `events` tool) and look for an entry under **your own author name**.
Every mutating verb journals one, so the journal is the record of what actually landed. Found it? It worked,
carry on. Not there? Then send it again. That check costs one call and is always correct, where a blind retry
is sometimes catastrophic.

Two answers you can trust completely, and it is worth knowing which is which:

- **"Rhino is busy: the UI thread is working"** now means the verb *never started*. It is safe to retry, and
  waiting a moment first is better than retrying immediately. Ask `pulse` - it answers even while the thread
  is held, and it says whether Rhino is working on something or stuck on a dialog, which want opposite
  responses.
- **"This started and has not finished after 5 minutes"** means the opposite: it is running and will finish.
  Never send it again. Read `/events` to see when it lands.

**Slow is not broken.** Every verb that touches the document runs on Rhino's single UI thread, one at a time.
If a human is dragging a slider, or another agent is half way through a `place`, yours waits behind it. That
is normal. `pulse` tells you what is happening; the answer to a queue is patience, not a second copy of your
request.

**Your edits mark the document modified**, the same as a human's, so the Grasshopper title carries an
asterisk and closing Rhino offers to save. That is a safety net, not a substitute for step 4: leaving work
for the human to notice at closing time means they get a prompt about a definition they did not write and
have to guess what was in it. Save when you are done and the prompt never appears. `canvas` reports
`modified` and `path`, so you can see the state rather than assume it.

**Order matters more than you would think.** Settle all the grouping and naming *first* - to rename or
recolour a group, call `group` again with its `id`, never ungroup-and-regroup - and only then call
`signature` once, followed by `arrange` and `review`. Repeated `signature` calls between regroupings
were how a canvas ended up with parallel chains sharing endpoints, where disconnecting a wire appeared to
do nothing and a later delete cut the live copy. It is idempotent now, but it is still a finishing move.
Keep group names to plain characters - write "and", not "&", and never an HTML entity.

**When something looks wrong structurally, look before you cut.** `wires` gives the whole connection
list; `peek` gives one parameter's data. `delete` refuses when it would cut wires to objects that stay
and names them - read that list instead of forcing it. And `undo` exists: a delete or an arrange can be
taken back, one step at a time.

**Composition, the rules the review enforces.** A canvas is read group by group, so the groups ARE the
abstraction layer.

1. **A group is a function, and does exactly one thing.** Single responsibility taken literally: if the
   name needs an "and", a comma or a slash, it is two groups - "halve the dimensions" and "compute the
   leg height" are two functions, not one "halves and heights". Name every group for the one thing it
   does. Moderate size means up to about thirty objects; past that, split it.
2. **A group is a virtual component: give it a signature.** Call `signature` on each group right after
   grouping. It plants named floating parameters just inside the left edge as inlets and at the right edge
   as outlets, and re-lands every crossing wire on them. Nothing crosses a boundary except through them -
   wiring one group's component straight into another's is reaching into a function body.
3. **Never rename a component.** A component's nickname is how everyone recognises it; renaming
   Multiplication to "W/2" makes the canvas unreadable for the next person and for you. Names belong on
   the floating parameters - that is what they are for. Only parameters get nicknames.
4. **Never position groups by hand; call `arrange`.** It lays groups out as whole blocks, the only way
   frames stay apart: a layout that places components individually interleaves the members of different
   groups, and interleaved members force their frames to overlap. Nest one level at most; a mother group
   sits at the very back of the draw order (`group` and `arrange` see to that).
5. **Colour by role** - four roles, no others (drawn at quarter opacity, so give full colours):
   **blue** `[70,110,255]` inputs the user may modify - the sliders themselves, and nothing more: do not
   duplicate a parameter to have a second copy of it lying around, because a copy that feeds nothing is
   just a thing the next reader has to check before ignoring. Do **not** collect every input into one bank
   either: a blue group belongs where its knobs are used, beside the function they feed, so a reader finds
   a knob where its effect is; **red** `[255,60,60]` the components whose geometry gets baked into
   Rhino as the product - select the group, bake, and everything needed is there; **yellow**
   `[255,220,0]` preview-only geometry, never baked; **grey** `[150,150,150]` a plain function. Flow
   left to right.
6. **Sliders deserve real domains.** `set` takes `minimum`/`maximum`/`decimals`, or a string value
   like `"0<1400<2400"` setting the whole domain at once - a bare 0..1 slider is almost always wrong.
   A constant goes in the socket that uses it - `set` with `param`, not a parameter and a wire - unless
   a reader needs to see it. A value typed into a socket is invisible on the canvas, so anything somebody
   would look for while reading the definition (a dimension, a tolerance, a name) belongs in a `Panel`
   wired in, where it can be seen and changed without opening anything. One value per panel: a panel is
   still a wire, not a place to pack several things as text.
7. **Respect the data tree; do not flatten your way out of trouble.** Grasshopper data is a tree of
   branches with paths like `{0;1}`, and a component runs once per item in the *longest* input, reusing
   the last item of the shorter ones - so a stray extra item does not error, it silently multiplies the
   geometry. Therefore:
   - **Keep paths clean and meaningful.** A path should say where a thing belongs (which desk, which leg),
     and match between trees you intend to pair. If two trees will not pair, fix the structure that made
     them, do not paper over it.
   - **Flatten and graft belong on the canvas as components**, not as little icons on a parameter: a
     reader must see where the structure changed. Never reach for flatten to make a mismatch go away - it
     throws away exactly the information the paths were carrying.
   - **Two wires into one socket meet only where their paths agree.** Grasshopper concatenates by path,
     so sources sitting at different depths - one on `{0}`, one on `{0;0}` - never land in the same
     branch: the component runs once per branch on half the data each time. Nothing turns red. A
     `Boundary Surfaces` handed an outline on `{0}` and its offset on `{0;0}` returns two separate
     surfaces instead of one with a hole in it, and looks right until somebody measures. `peek` the
     *input* after wiring several sources into one socket, not the outputs that feed it; `review` calls
     this "mismatched paths" and calls it blocking.
   - **Never use the simplify modifier. Ever.** It quietly drops path components depending on what the
     tree happens to look like at that moment, so the same definition behaves differently on different
     data - a bug that appears in someone else's file, months later. If a structure needs changing, change
     it visibly, with a component.
   - **Build for many, even when asked for one.** If the brief says one desk, make the definition work for
     a list of desks: one branch per desk, structure preserved end to end. That costs nothing when done
     from the start and is a rewrite when retrofitted.
   - **Data travels on wires, one value per wire - never as text.** Do not use Format or Concatenate to
     pack several numbers into a string and feed that to a numeric input: it fails as "Data conversion
     failed from Text to Number", and even when a conversion succeeds you have thrown the structure away.
     To gather several values into one list, use Merge. To pair values, wire them into separate inputs.
   - **The pattern for making many of something.** Say you build N units, each with M shelves. Put the N
     per-unit values into N branches (`Merge` the sliders, then `Graft Tree` - one branch per unit).
     Per-unit arithmetic then broadcasts by itself. Where a list that is the *same for every unit* (the M
     shelf heights, the rung positions) has to meet the per-unit branches, plain matching will not cross
     two differently-shaped trees: use **`Cross Reference`**, which pairs every A with every B and gives
     you M items inside each unit's branch. Getting this wrong is what silently produces 1806 of something.
   - **Verify with `peek`** after each group: branch count and item counts are the specification.
     `review` flags an item-access input holding several items in a branch, which is this failure
     exactly - and it reports every red or orange component, so bringing review to zero means the
     definition actually runs.
8. **Build with components, not script.** A definition is made of components - that is what makes it
   readable and editable by whoever opens it next. Reach for a C# script component only when no
   combination of components can do the job, and say why when you do.
9. **Leave no dead ends.** Every component and parameter either feeds something or draws something. A
   parameter left over from a rethink, a component whose output went elsewhere - each is a thing the next
   reader must check before ignoring. `review` lists them as "unused"; delete them.
10. **When a tool fights you, say so.** Call `report` with what you expected against what happened -
   refused requests log themselves, this is for the rest. If the human hits repeated trouble, **ask them**
   whether to prepare a report they can mail (`feedback` assembles it and returns a mailto); never send
   anything yourself, and never call `feedback` without asking first.
11. **Finish with `arrange`, then `review`, and fix what review says.** It measures what can be
   measured - overlapping frames, unnamed groups, names confessing two jobs, oversized groups, renamed
   components, bare boundary crossings, ungrouped objects - so a definition converges instead of being
   hoped over. Leave notes in panels where a reader will need them.
12. **Then quiet the preview, and only then save.** A definition that is built and checked previews
   everything it ever computed - the cutting boxes a difference already ate, the construction curves, the
   profile that was extruded away - and the human is left picking the product out of the scaffolding, or
   reads the scaffolding as the answer. The colours already said which geometry was ever meant to be seen,
   so **`preview` with no id** sweeps the document on exactly that rule: only the outlets of the **red**
   groups (baked as the product) and the **yellow** ones (there to be looked at) keep drawing, and
   everything else goes dark - grey functions, blue knobs, every intermediate inside every group. Naming a
   group quiets that one on its own terms whatever colour it wears, and `on:true` gives a group its whole
   preview back when you need to look inside again.

**But do not wait until step 12 if the viewport is already unusable.** `preview` takes a single object's id
as readily as a group's, and `ids` takes a list of either, so the moment an intermediate output floods the
view - a list of points, a field of construction lines - quiet that component and carry on. This is worth
knowing because of how badly it goes otherwise: a facade of 960 panels interpolated through 24 points each
put **23,040 point markers** over the building, and neither the human nor any screenshot could see the
product underneath. The author looked for a preview flag on `set` and on `param`, did not find one, and
concluded it could not be done - while the verb that does it had been there all along. Drawing is not a
parameter. It is this verb.

## Without the MCP tools

Everything above is the same protocol either way, so none of it is wasted - only the door changes. Read this
if your host has no `grasshopper` tools, and stop reading it the moment it does.

**Starting a session is the part you cannot guess, so here it is exactly.** There is no verb for it: the
server lives *inside* Grasshopper, so nothing can answer until Grasshopper is running, which is why `launch`
sits in the MCP layer and not in the protocol. Two mistakes are easy here and both leave you with a Rhino
that will never answer:

```powershell
# 1. Note which sessions exist BEFORE you start anything.
Get-ChildItem "$env:TEMP\phenome-link-*.port"

# 2. Start Rhino AND Grasshopper. Rhino alone is not enough - the plugin loads with Grasshopper,
#    so no Grasshopper means no canvas link, ever. Quote the WHOLE argument, exactly like this:
#    an inner "_Grasshopper" gets its quotes doubled by some shells and launchers, Rhino then runs
#    no script at all, and you are left waiting for a port file that will never be written.
& "C:\Program Files\Rhino 8\System\Rhino.exe" /nosplash "/runscript=_Grasshopper"

# 3. Wait for a port file that was NOT in the list from step 1. Rhino takes its time - poll every
#    3 seconds, give it up to 90. Attaching to a port that was already there puts you on somebody
#    else's canvas, which is the one failure worse than not starting at all.
# 4. GET http://127.0.0.1:<that port>/ describes every verb, its arguments and its answers.
```

A `phenome-rhino-<pid>.port` appears too, on its own port: that is the Rhino half, and it answers about the
process rather than the canvas - `GET /pulse` for whether Rhino is idle, busy or blocked, which works even
while the UI thread is held. If you see only that file and no `phenome-link-<pid>.port`, step 2's script did
not run and Grasshopper never opened: that is the failure to recognise, and the fix is not to wait longer.

If you cannot start a process at all, or step 2 keeps giving you a Rhino with no canvas, **ask the human to
open Rhino and Grasshopper and tell you when it is up.** One sentence, costs a few seconds, and cannot go
wrong - much better than a second and a third attempt leaving Rhinos open behind you.

The port file holds nothing but the number. One file per Rhino; a stale one names a dead pid, so a port that
does not answer is a leftover and not a fault.

**Then the rules that apply whichever door you came through.** Put your own name in `author` on every POST -
the journal records it, that is how you skip your own echo and how anybody tells two agents apart. Every verb
in this file is an endpoint of the same name: `POST /place`, `POST /wire`, `GET /peek?id=`, `GET /review`,
`POST /arrange`, `POST /preview`, `POST /save`. The rest - the journal's cursor, its gaps, what each verb
answers - is in `GET /` and, at length, in the plugin's `docs/protocol.md`.
