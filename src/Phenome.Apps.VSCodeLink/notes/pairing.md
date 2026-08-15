## Pairing with Grasshopper (Phenome Link)

**Prefer the MCP tools.** The `grasshopper` server registers one per verb and your session already
lists them with their arguments, so they are not repeated here. Four habits that list cannot teach you:
search `components` before `add` when unsure of a name; prefer `place` over add/wire loops; verify
with `peek`, not `screenshot`, since the canvas carries positions and needs no picture; and use
`launch` when there is no session rather than starting Rhino yourself. A tool missing from your session
means the session predates the current server - restart it instead of falling back to raw HTTP.

**The components you will reach for, with their exact input names** - so you need not search for them.
Anything unusual: ask `components`, and `describe` tells you a placed object's real parameters.

| what | component | inputs |
| --- | --- | --- |
| a knob | `Number Slider` | (set its domain with `set`) |
| count out a series | `Series` | Start, Step, Count |
| divide a span | `Range` | Domain, Steps |
| arithmetic | `Addition` `Subtraction` `Multiplication` `Division` | A, B |
| a point | `Construct Point` | X coordinate, Y coordinate, Z coordinate |
| take a point apart | `Deconstruct Point` | Point |
| a line | `Line` | Start Point, End Point |
| a line from a direction | `Line SDL` | Start, Direction, Length |
| a box | `Box 2Pt` | Point A, Point B, Base |
| a box about a centre | `Center Box` | Base, X, Y, Z |
| gather lists into one | `Merge` | Data 1, Data 2, … (zoom adds more) |
| move something | `Move` | Geometry, Motion |
| a vector | `Unit X` `Unit Y` `Unit Z` | Factor |
| change tree structure | `Flatten Tree` `Graft Tree` | Tree |
| pair every A with every B | `Cross Reference` | List (A), List (B) |
| colour the preview | `Custom Preview` | Geometry, Material |
| a colour | `Colour Swatch` | (set its value) |
| a note on the canvas | `Panel` | (set its text) |

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

If the tools are ever unavailable, the same protocol is plain HTTP: the port is in
`%TEMP%\phenome-link-<pid>.port` (one file per Rhino, a stale one has a dead pid) and `GET /`
describes every verb. Put your own name in `author` on every POST and skip your own echo. The rest -
the journal's cursor, its gaps, the rules the verbs share - is in the plugin's `docs/protocol.md`.
