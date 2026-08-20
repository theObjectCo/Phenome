<!--
Draft of the Rhino Discourse announcement for 0.23.0. Kept in the repository because a release
post is part of releasing, and the next one is easier to write with the last one in front of you.
Held under 2000 characters on purpose: a forum reads a short post and skims a long one, and every
paragraph that survives that limit is one somebody actually needs. Paste the releases link where
it says so - and check the "what it is not" list still tells the truth before posting it again.
-->

**Runaround: a saddle for a live Rhino — Phenome Link 0.23.0**

Hand a language model a Grasshopper canvas and no protocol, and it drops forty components on the
origin, wires the wrong sockets, and reports success. Nothing told it what success looks like, and
nothing let it look.

The picture is the specification: a cybernetic cowboy on a live rhinoceros. The animal is not a
machine — one mind, one thing at a time, and now and then it stops dead waiting for somebody to
dismiss a dialog. You do not automate an animal. You ride it, and the engineering is all tack.

Phenome Link is the tack: a Grasshopper plugin speaking HTTP on loopback, a Rhino plugin watching
for the moments it stops answering, an MCP server turning both into tools. It enforces the rules a
canvas has no compiler to enforce — a group is a function and does one thing, a group declares a
signature and nothing crosses its boundary otherwise, colour is a role and not decoration. `review`
measures that and says where a definition falls short. Nothing is ever positioned by hand.

New in 0.23.0: `arrange` places notes too — a note's group is what it is *about*, so in a group it
becomes the caption, loose it becomes the title. `arrange` is finally idempotent; it used to walk
the whole definition 26×52 px per run. An agent can read a note's text and rectangle back instead
of writing blind. And a verb reported as failed no longer turns out to have run.

Install: **Unblock** both files (Properties → Unblock), drop the `.gha` and `.rhp` into your
Grasshopper Components folder, restart Rhino. `.vsix` for VS Code. Rhino 8, Windows.

Honest limits: it is early. Loopback with no authentication, so not on a shared machine. One UI
thread, so slow is slow for everybody. And it makes a capable model *verifiable* — it does not make
a weak one good.

Download: **[paste the releases link here]**
