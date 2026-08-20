#!/usr/bin/env node
// The Grasshopper link as an MCP server: the same loopback HTTP protocol, wrapped so that agents see named
// tools instead of shell commands. The point is permissions as much as ergonomics - a raw Invoke-RestMethod
// is a different command every time and gets challenged every time, while grasshopper__say is one name a
// user allows once.
//
// Deliberately dependency-free: newline-delimited JSON-RPC on stdio, discovery by the same port files the
// rest of the family uses. Copied into a workspace by the Phenome Link VS Code extension ("Teach Agents"),
// but runs anywhere node runs - the extension is a courier, not a dependency.

const fs = require('fs');
const os = require('os');
const path = require('path');
const { spawn } = require('child_process');

let port = null;
let rhinoPort = null;

/// A canvas session picked deliberately through the sessions tool, rather than by whichever answered first.
/// Outranks discovery for as long as it keeps answering; see discover().
let chosen = null;

// ------------------------------------------------------------------------------------------- link client

async function ask(pathname, body) {
    if (port === null) {
        await discover();
    }

    if (port === null) {
        throw new Error('No Grasshopper session. Use grasshopper__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${port}${pathname}`, {
            method: body ? 'POST' : 'GET',
            body: body ? JSON.stringify({ author: 'claude', ...body }) : undefined,
        });

        return await answer.json();
    } catch (failed) {
        port = null;
        throw new Error(`The Grasshopper session stopped answering (${failed.message}). Try again to rediscover.`);
    }
}

/// The same call, but to the Rhino half of the link rather than the canvas half.
///
/// Two servers, because they answer about two different things and one of them exists when the other does
/// not: the canvas link is born with Grasshopper, the Rhino link with Rhino. Anything that is about the
/// process or the document - commands, the document summary, the command line, whether a dialog is up -
/// belongs here, and works in a Rhino that never opened a canvas.
///
/// Falls back to the canvas link rather than failing, so an older plugin pairing keeps working: those
/// verbs were on the canvas link first and are still there.
async function askRhino(pathname, body) {
    if (rhinoPort === null) {
        await discoverRhino();
    }

    if (rhinoPort === null) {
        if (port !== null || (await sessions()).length > 0) {
            // On the canvas link both of these live at /rhino - one verb, told apart by the method.
            const old = pathname === '/command' || pathname === '/doc' ? '/rhino' : pathname;

            return ask(old, body);
        }

        throw new Error('No Rhino session. Use grasshopper__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${rhinoPort}${pathname}`, {
            method: body ? 'POST' : 'GET',
            body: body ? JSON.stringify({ author: 'claude', ...body }) : undefined,
        });

        // A 404 from a link that is plainly alive means an older plugin that never had this verb. The
        // canvas link has had them all along, so ask there rather than telling the agent the door is
        // shut - a mixed pairing is the normal state of a machine that updates one half at a time.
        if (answer.status === 404 && (await canvas())) {
            const old = pathname === '/command' || pathname === '/doc' ? '/rhino' : pathname;

            return ask(old, body);
        }

        return await answer.json();
    } catch (failed) {
        rhinoPort = null;
        throw new Error(`The Rhino session stopped answering (${failed.message}). Try again to rediscover.`);
    }
}

/// Whether there is a canvas link to fall back to.
async function canvas() {
    if (port !== null) {
        return true;
    }

    await discover();

    return port !== null;
}

/// Every live Rhino link on this machine, newest file first - one per running Rhino, canvas or no canvas.
async function rhinoSessions() {
    let files = [];

    try {
        files = fs.readdirSync(os.tmpdir())
            .filter(f => /^phenome-rhino-\d+\.port$/.test(f))
            .map(f => path.join(os.tmpdir(), f))
            .map(f => ({ f, at: fs.statSync(f).mtimeMs }))
            .sort((a, b) => b.at - a.at)
            .map(entry => entry.f);
    } catch {
        return [];
    }

    const live = [];

    for (const file of files) {
        try {
            const candidate = parseInt(fs.readFileSync(file, 'utf8').trim(), 10);
            const answer = await fetch(`http://127.0.0.1:${candidate}/`, { signal: AbortSignal.timeout(1500) });
            const hello = await answer.json();

            if (hello?.phenome === 'rhino-link') {
                live.push({ port: candidate, pid: parseInt(/-(\d+)\.port$/.exec(file)?.[1] ?? '0', 10) });
            }
        } catch {
            // Stale file or foreign server; keep looking.
        }
    }

    return live;
}

/// The Rhino link belonging to the canvas we are on, or the newest one when there is no canvas.
///
/// Same process, so the pid pairs them: talking to one Rhino's canvas and another Rhino's command line
/// would be worse than having no command line at all.
async function discoverRhino() {
    const live = await rhinoSessions();

    if (live.length === 0) {
        rhinoPort = null;
        return;
    }

    if (port !== null) {
        const canvas = (await sessions()).find(one => one.port === port);
        const paired = canvas && live.find(one => one.pid === canvas.pid);

        rhinoPort = paired ? paired.port : null;

        if (rhinoPort !== null) {
            return;
        }
    }

    rhinoPort = live[0].port;
}

/// Every live session on this machine, newest file first.
///
/// One Rhino per port file, and several Rhinos are the point: two agents working at once want a canvas
/// each, not the same one. PHENOME_GH_PORT pins this server to one of them - the extension sets it when it
/// starts an agent for a particular canvas - and without it the newest session wins, which is the one a
/// human just opened.
async function sessions() {
    let files = [];

    try {
        files = fs.readdirSync(os.tmpdir())
            .filter(f => /^phenome-link-\d+\.port$/.test(f))
            .map(f => path.join(os.tmpdir(), f))
            .map(f => ({ f, at: fs.statSync(f).mtimeMs }))
            .sort((a, b) => b.at - a.at)
            .map(entry => entry.f);
    } catch {
        return [];
    }

    const live = [];

    for (const file of files) {
        try {
            const candidate = parseInt(fs.readFileSync(file, 'utf8').trim(), 10);
            const answer = await fetch(`http://127.0.0.1:${candidate}/`, { signal: AbortSignal.timeout(1500) });
            const hello = await answer.json();

            if (hello?.phenome === 'grasshopper-link') {
                live.push({ port: candidate, pid: parseInt(/-(\d+)\.port$/.exec(file)?.[1] ?? '0', 10) });
            }
        } catch {
            // Stale file or foreign server; keep looking.
        }
    }

    return live;
}

async function discover() {
    const pinned = parseInt(process.env.PHENOME_GH_PORT ?? '', 10);

    if (pinned) {
        port = pinned;
        return;
    }

    const live = await sessions();

    // A session chosen through the sessions tool outranks picking the first that answers, and has to be
    // re-honoured here because this runs again after any failed call. Dropped once it stops answering, so a
    // choice cannot outlive the Rhino it named.
    if (chosen !== null && live.some(one => one.port === chosen)) {
        port = chosen;
        return;
    }

    chosen = null;

    // Cleared, not merely left alone, when nothing answers: a port we last spoke to belongs to a Rhino
    // that may since have died, and holding onto it makes every later call - launch most of all - argue
    // that a session exists when the machine plainly has none.
    port = live.length > 0 ? live[0].port : null;
}

async function launch(fresh, withGrasshopper = true) {
    if (!withGrasshopper) {
        return launchRhinoAlone(fresh);
    }

    const before = (await sessions()).map(one => one.port);

    if (!fresh) {
        await discover();

        // Refuse only for a session that is actually answering. A pin (PHENOME_GH_PORT) survives the
        // Rhino it named, so checking `port` alone would leave an agent whose canvas has died unable to
        // start another one - the one moment it most needs to.
        if (port !== null && before.includes(port)) {
            return `A session already runs on port ${port}.`;
        }

        port = null;
    }

    const rhino = 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe';

    if (!fs.existsSync(rhino)) {
        throw new Error(`Rhino 8 is not at ${rhino}; start it by hand and try again.`);
    }

    // Verbatim, because node's default quoting doubles the quotes _Grasshopper needs and Rhino then runs
    // no script at all - the plugin loads with Grasshopper, so no Grasshopper means no link, ever.
    spawn(rhino, ['/nosplash', '/runscript="_Grasshopper"'], {
        detached: true,
        stdio: 'ignore',
        windowsVerbatimArguments: true,
    }).unref();

    // Rhino takes its time; a port that was not there before is the sign of life. Waiting for *a* session
    // would otherwise hand back the one already running and quietly put two agents on one canvas.
    for (let waited = 0; waited < 90_000; waited += 3000) {
        await new Promise(rest => setTimeout(rest, 3000));

        const now = await sessions();
        const born = now.find(one => !before.includes(one.port));

        if (born) {
            port = born.port;

            // A pin names this agent's canvas, and this is now that canvas. Left pointing at the Rhino
            // that died, the pin would win every later rediscovery and send the agent back to a port
            // nothing answers on - a session that repairs itself once and then breaks for good.
            if (process.env.PHENOME_GH_PORT) {
                process.env.PHENOME_GH_PORT = String(port);
            }

            return `Rhino is up; the link answers on port ${port} (process ${born.pid}). `
                + `${now.length} session(s) live on this machine.`;
        }
    }

    throw new Error('Rhino started but the link never answered - is the Phenome Link plugin installed?');
}

/// Rhino on its own: no Grasshopper, no canvas, and nothing waiting on one.
///
/// Worth having as its own door because most of what an agent does to Rhino - open a file, select, run a
/// command, export - has nothing to do with a definition. Starting Grasshopper for that costs a slower
/// launch and a second plugin that can fail to load, in exchange for a canvas nobody looks at.
async function launchRhinoAlone(fresh) {
    const before = (await rhinoSessions()).map(one => one.port);

    if (!fresh) {
        await discoverRhino();

        if (rhinoPort !== null && before.includes(rhinoPort)) {
            return `A Rhino session already answers on port ${rhinoPort}.`;
        }

        rhinoPort = null;
    }

    const rhino = 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe';

    if (!fs.existsSync(rhino)) {
        throw new Error(`Rhino 8 is not at ${rhino}; start it by hand and try again.`);
    }

    spawn(rhino, ['/nosplash'], { detached: true, stdio: 'ignore', windowsVerbatimArguments: true }).unref();

    for (let waited = 0; waited < 90_000; waited += 3000) {
        await new Promise(rest => setTimeout(rest, 3000));

        const now = await rhinoSessions();
        const born = now.find(one => !before.includes(one.port));

        if (born) {
            rhinoPort = born.port;

            return `Rhino is up without Grasshopper; the Rhino link answers on port ${rhinoPort} `
                + `(process ${born.pid}). ${now.length} Rhino link(s) live on this machine. `
                + 'Canvas tools will not work here - launch again for one.';
        }
    }

    throw new Error('Rhino started but the Rhino link never answered - is the Phenome Link plugin installed?');
}

// ------------------------------------------------------------------------------------------------ tools

const object = (properties, required) => ({ type: 'object', properties, ...(required ? { required } : {}) });
const str = description => ({ type: 'string', description });
const flag = description => ({ type: 'boolean', description });
const ids = description => ({ type: 'array', items: { type: 'string' }, description });

const TOOLS = [
    {
        name: 'canvas',
        description: "The Grasshopper document. as:'mermaid' gives a flowchart instead - groups as subgraphs, red components marked, with a map of short node ids to real guids: the shape of a definition at a fiftieth of the size, and the best way to orient yourself in one you did not build. It carries no data, so branch and item counts still come from peek. Omit 'as' for the full state: every object, wires, values, selection, enabled, preview, data mapping, solver.",
        inputSchema: object({ as: { type: 'string', enum: ['mermaid'], description: 'Omit for the full state.' } }),
        run: args => ask(args.as === 'mermaid' ? '/canvas?as=mermaid' : '/canvas'),
    },
    {
        name: 'events',
        description: "The journal after entry N. The response's 'latest' is your next cursor; entries carry author - skip your own ('claude'). The human's messages arrive as kind:'message'.",
        inputSchema: object({ since: { type: 'number', description: 'Your cursor; 0 for everything kept.' } }, ['since']),
        run: args => ask(`/events?since=${args.since}`),
    },
    {
        name: 'pulse',
        description: "Whether Rhino is idle, busy or blocked. Answered without the Rhino UI thread, so it still answers when nothing else does - which is the point. When another tool times out, this says which of two opposite situations you are in: 'busy' names the running command and how long it has run, and means wait; 'blocked' names the open dialog, and means nothing will answer until a human clicks it.",
        inputSchema: object({}),
        run: () => askRhino('/pulse'),
    },
    {
        name: 'dismiss',
        description: "Answer the dialog Rhino is waiting on: press a button by name, or close it when no name is given. pulse names the dialog and lists its buttons. Closing is declining, so it is the default; pressing a button is agreeing to something and has to be asked for. Pass 'expect' with the dialog's title and it refuses if a different one is up by then - dialogs are transient, and a blind press answers whatever happens to be there.",
        inputSchema: object({
            button: str("The button to press, exactly as pulse lists it. Omit to close the dialog instead."),
            key: str("A key to type instead of clicking - needed when pulse says clickable:false, which means the dialog draws its own buttons and has nothing to click. Use the underlined letter of the answer you want."),
            expect: str("The dialog title you meant to answer; refuses if another one is open."),
        }),
        run: args => askRhino('/dismiss', args),
    },
    {
        name: 'escape',
        description: "Post Escape to Rhino, cancelling whatever it is waiting for. Use when pulse says 'busy' and the command it names is one that wants a click - a scripted interactive command sits asking for a pick no script will supply, and then every tool here reports 'busy' as though waiting would help. dismiss cannot answer that case: a command waiting on a pick is not a dialog, so there is no window to click. Ask pulse afterwards to see whether it took; the key is queued, not delivered.",
        inputSchema: object({
            times: { type: 'number', description: 'How many levels to cancel; one by default, up to five.' },
        }),
        run: args => askRhino('/escape', { times: args.times ?? 1 }),
    },
    {
        name: 'console',
        description: "The tail of Rhino's own command line: what commands and scripts actually said - selection counts, script prints, the reason a command did something surprising. Read it after anything whose result is not in the response. It is drained when the UI thread breathes, so a long command's output arrives when that command ends; use pulse for what is happening right now. Pass mine:true for the link's own lines instead, which this leaves out so an agent does not read its own requests back as Rhino's answers - read those when the suspicion is that the bridge rather than Rhino is at fault.",
        inputSchema: object({
            tail: { type: 'number', description: 'How many lines back, 1 to 500; default 50.' },
            mine: { type: 'boolean', description: "True for the link's own lines rather than Rhino's." },
        }),
        run: args => args.mine
            ? ask(`/console?tail=${args.tail ?? 50}&mine=true`)
            : askRhino(`/console?tail=${args.tail ?? 50}`),
    },
    {
        name: 'say',
        description: 'A message into the journal, for the human or another agent.',
        inputSchema: object({ text: str('What to say.'), to: str('Optional addressee.') }, ['text']),
        run: args => ask('/say', args),
    },
    {
        name: 'components',
        description: 'Search the installed component catalogue by name or description. Top matches carry their true inputs and outputs - use this before add when unsure of the exact ribbon name.',
        inputSchema: object({ q: str('What to look for, e.g. "divide curve".') }, ['q']),
        run: args => ask(`/components?q=${encodeURIComponent(args.q)}`),
    },
    {
        name: 'add',
        description: "Put a component or parameter on the canvas by name (e.g. 'Number Slider', 'Construct Point') or guid. Answers its id.",
        inputSchema: object({
            name: str('Component name as the ribbon shows it.'),
            guid: str('Component guid, when the name is ambiguous.'),
            pivot: { type: 'array', items: { type: 'number' }, description: '[x, y] on the canvas.' },
            nickname: str('Only for parameters - never rename a component, it makes the canvas unreadable.'),
        }),
        run: args => ask('/add', args),
    },
    {
        name: 'wire',
        description: "Connect outputs to inputs. PASS THEM ALL AT ONCE in 'wires' - a definition is mostly wires, and one call each means one round trip and one canvas recompute each. Ends are {id, param?}, where param is a name or index and is needed when a component has several on that side; disconnect:true takes a wire back. A single {from, to} at the root still works for a one-off.",
        inputSchema: object({
            wires: {
                type: 'array',
                description: 'Every wire to make: [{from:{id, param?}, to:{id, param?}, disconnect?}]',
                items: { type: 'object' },
            },
            from: object({ id: str('Source object id.'), param: str('Output name or index.') }, ['id']),
            to: object({ id: str('Target object id.'), param: str('Input name or index.') }, ['id']),
            disconnect: flag('True removes the wire instead.'),
        }),
        run: args => ask('/wire', args),
    },
    {
        name: 'set',
        description: "Values into objects. PASS THEM ALL AT ONCE in 'values'; a single one at the root works for a one-off. A slider takes bounds and precision, or a string like '0<50<100' for all three. With 'param', the value replaces a component input's stored constant - no standalone parameter and wire for the number two - and a null value empties that socket, which is the only way back to nothing stored. On a note - a Scribble or a Panel - the value is its wording, which makes this the way to reword a note you already placed rather than deleting and rebuilding it; empty or whitespace is refused, because a blank note looks exactly like one whose text went missing.",
        inputSchema: object({
            values: {
                type: 'array',
                description: 'Every value to set: [{id, value, param?, minimum?, maximum?, decimals?}]',
                items: { type: 'object' },
            },
            id: str('Object id.'),
            value: { description: "Number, text or flag. For sliders, a string '<min><<value><<max>' sets the whole domain." },
            param: str("A component input's name or index - the value becomes that input's stored constant."),
            minimum: { type: 'number', description: 'Slider lower bound.' },
            maximum: { type: 'number', description: 'Slider upper bound.' },
            decimals: { type: 'number', description: 'Slider decimal places; 0 makes it an integer slider.' },
        }),
        run: args => ask('/set', args),
    },
    {
        name: 'arrange',
        description: "Lay the whole document out in layers, mermaid-style: sources left, few crossings, even spacing. Groups are laid out as whole blocks, so their frames never overlap. Notes are placed too, by their group: a note in a group becomes that group's caption and goes above its other members, a note in no group becomes the document's title and goes above everything - so you never position one yourself, you just say which group it is about when you place it. Idempotent: running it on a settled document answers moved:0 and changes no coordinates, so call it as often as you like. Run it after building or editing, and after grouping - never place anything by hand.",
        inputSchema: object({}),
        run: () => ask('/arrange', {}),
    },
    {
        name: 'signature',
        description: "Gives a group named floating parameters at its edges and re-lands every crossing wire on them, so it reads as a virtual component. Safe to run twice - it recognises the ports it planted and reuses them - but run it ONCE, after all the grouping and renaming is settled: it is a finishing move, not something to sprinkle. Omit id to do every group.",
        inputSchema: object({ id: str('Group id; omit for all groups.') }),
        run: args => ask('/signature', args),
    },
    {
        name: 'preview',
        description: "Quiets the preview, so the viewport shows the product instead of every step that made it - the cutting boxes, the construction curves, the profile that was extruded away. Called with no id it sweeps the whole document: only the outlets of the RED and YELLOW groups keep drawing, and everything else goes dark. That is the finishing move - run it once the definition is built and verified, after review and before save. Name a group instead to quiet just that one, whatever colour it wears; on:true gives a group its whole preview back when you need to look inside again.",
        inputSchema: object({
            id: str('Group id; omit to sweep the document, leaving only red and yellow groups\' outlets drawing.'),
            on: { type: 'boolean', description: 'True shows everything in the group again instead of quieting it.' },
        }),
        run: args => ask('/preview', args),
    },
    {
        name: 'review',
        description: "Lints the definition. Every finding carries a severity: 'blocking' means the definition does not run or does the wrong thing - red components, an item input holding several items, an object in two groups, a hidden flatten/graft or simplify, a group with no signature - and those must all be fixed. 'polish' means manners: group sizes, input banks, unnamed groups, ungrouped objects. Fix the blocking ones first and never abandon a working graph to chase polish.",
        inputSchema: object({}),
        run: () => ask('/review'),
    },
    {
        name: 'group',
        description: "A named group - a function. DECLARE ITS SIGNATURE FIRST: pass inlets and outlets and they are created as named floating parameters, answered as a name-to-id map ('ports'), so you can then place the body and wire it onto them. That is the order to build in: plan, declare every group with its signature, fill the bodies, review. Colour by role, four roles only (drawn at quarter opacity): blue [70,110,255] inputs the user may modify, red [255,60,60] components baked to Rhino as the product, yellow [255,220,0] preview-only geometry, grey [150,150,150] a plain function.",
        inputSchema: object({
            name: str('The one thing this group does.'),
            inlets: { type: 'array', description: "This group's inputs: names, or {name, type} where type is number, integer, text, boolean, point, vector, plane, line, curve, surface, brep, mesh, geometry, interval, colour or transform.", items: {} },
            outlets: { type: 'array', description: "This group's outputs, same shape as inlets.", items: {} },
            id: str('An existing group to rename, recolour or add members to - use this instead of ungrouping and regrouping.'),
            ids: ids('Existing objects to enclose, when you are grouping after the fact.'),
            colour: { type: 'array', items: { type: 'number' }, description: '[r, g, b] - pick by the role convention in the description.' },
        }, ['name']),
        run: args => ask('/group', args),
    },
    {
        name: 'ungroup',
        description: 'Dissolve a group, keeping its members.',
        inputSchema: object({ id: str('Group id.') }, ['id']),
        run: args => ask('/ungroup', args),
    },
    {
        name: 'select',
        description: 'Select objects on the canvas, replacing the selection unless add:true.',
        inputSchema: object({ ids: ids('Object ids.'), add: flag('Keep the existing selection.') }, ['ids']),
        run: args => ask('/select', args),
    },
    {
        name: 'delete',
        description: "Remove objects. It refuses and names the wires first when deleting would cut connections to objects that stay - read that list rather than reaching for force, because it is telling you the objects are not as idle as they look. force:true means you meant it. Anything it did can be taken back with undo.",
        inputSchema: object({
            ids: ids('Object ids.'),
            force: flag('Delete even though live wires would be cut.'),
        }, ['ids']),
        run: args => ask('/delete', args),
    },
    {
        name: 'describe',
        description: "One placed object's parameters: names, nicknames, types, item-or-list access, how many wires and items each holds. Use this on something already on the canvas instead of searching the catalogue for its parameter names. On a note - a Scribble or a Panel - it also answers 'annotation' with the text as it actually reads, where it sits, the rectangle it covers and the group it is in: use that to check your own wording and placement landed without needing a screenshot.",
        inputSchema: object({ id: str('Object id.') }, ['id']),
        run: args => ask(`/describe?id=${args.id}`),
    },
    {
        name: 'wires',
        description: "Every wire in the document, from and to, with names and parameter names - the whole picture, which asking input by input never adds up to. Use it after any structural change to see what is actually connected.",
        inputSchema: object({}),
        run: () => ask('/wires'),
    },
    {
        name: 'undo',
        description: "One step back through Grasshopper's own undo stack - every verb records into it, so a delete or a bad arrange can be taken back. Answers the name of the step undone.",
        inputSchema: object({}),
        run: () => ask('/undo', {}),
    },
    {
        name: 'redo',
        description: 'One step forward again.',
        inputSchema: object({}),
        run: () => ask('/redo', {}),
    },
    {
        name: 'param',
        description: 'Data mapping on one parameter: flatten, graft, simplify, reverse.',
        inputSchema: object({
            id: str('Object id.'),
            side: { type: 'string', enum: ['input', 'output'] },
            param: str('Parameter name or index.'),
            mapping: { type: 'string', enum: ['none', 'flatten', 'graft'] },
            simplify: flag('Simplify the tree.'),
            reverse: flag('Reverse the lists.'),
        }, ['id']),
        run: args => ask('/param', args),
    },
    {
        name: 'solver',
        description: 'Lock or unlock the Grasshopper solver.',
        inputSchema: object({ enabled: flag('True runs, false locks.') }, ['enabled']),
        run: args => ask('/solver', args),
    },
    {
        name: 'bake',
        description: 'Bake objects into the Rhino document.',
        inputSchema: object({ ids: ids('Object ids to bake.') }, ['ids']),
        run: args => ask('/bake', args),
    },
    {
        name: 'scripts',
        description: 'The script components on the canvas, with their generation.',
        inputSchema: object({}),
        run: () => ask('/scripts'),
    },
    {
        name: 'script_read',
        description: "One script component's source.",
        inputSchema: object({ id: str('Script component id.') }, ['id']),
        run: args => ask(`/script?id=${args.id}`),
    },
    {
        name: 'script_write',
        description: "New source into a script component; answers with the component's own compile errors and warnings.",
        inputSchema: object({ id: str('Script component id.'), source: str('The whole new source.') }, ['id', 'source']),
        run: args => ask('/script', args),
    },
    {
        name: 'new_document',
        description: 'A fresh Grasshopper document on the canvas.',
        inputSchema: object({}),
        run: () => ask('/new', {}),
    },
    {
        name: 'open',
        description: 'Open a .gh on the canvas, or a .3dm in Rhino.',
        inputSchema: object({ path: str('Absolute path.') }, ['path']),
        run: args => ask('/open', args),
    },
    {
        name: 'report',
        description: "Leave a note where a verb fought you: what you expected against what happened. Refused requests log themselves, so this is for the rest - a tool that technically worked but not as its description promised. Costs nothing, goes to a local file, and is how the bridge gets fixed.",
        inputSchema: object({
            expected: str('What you expected to happen.'),
            got: str('What happened instead.'),
            notes: str('Anything else worth knowing.'),
        }, ['expected', 'got']),
        run: args => ask('/report', args),
    },
    {
        name: 'feedback',
        description: "Assembles the whole complaint into one readable file - session, composition review, recent friction log - and answers with its path and a mailto link. ASK THE HUMAN FIRST: offer it when they have hit repeated trouble, and let them send it themselves. Nothing is sent from here; the mailto opens their mail client with everything filled in and the file to attach.",
        inputSchema: object({
            expected: str('What was expected.'),
            got: str('What happened instead.'),
            to: str('Recipient; omit for the default intake address.'),
        }, ['expected', 'got']),
        run: args => ask('/feedback', args),
    },
    {
        name: 'friction',
        description: 'The friction log: refused requests and reports, newest last, with the file path.',
        inputSchema: object({ tail: { type: 'number', description: 'How many entries; default 50.' } }),
        run: args => ask(`/friction?tail=${args.tail ?? 50}`),
    },
    {
        name: 'launch',
        description: "Start Rhino with Grasshopper and wait for the link to answer. Use when there is no session. With fresh:true it starts another Rhino even though one is already running and works with that one - which is how two agents each get a canvas of their own instead of editing the same one. With grasshopper:false it starts Rhino alone - faster, and enough for anything that is about the document rather than a definition: open, select, run commands, export.",
        inputSchema: object({
            fresh: flag('Start another Rhino and use it, even if a session exists.'),
            grasshopper: flag('False starts Rhino without Grasshopper; canvas tools then have nothing to talk to.'),
        }),
        run: args => launch(args.fresh === true, args.grasshopper !== false),
    },
    {
        name: 'sessions',
        description: "Every live session on this machine and which ones these tools are talking to. Two lists, because there are two links: a canvas session per running Grasshopper, and a Rhino session per running Rhino - a Rhino started without Grasshopper appears only in the second, and commands, pulse and the console still work there. Pass 'use' with a port to send every later call to that session: with two Rhinos open the choice was made for you by whichever answered first, which is how an agent comes to edit the canvas it was not looking at.",
        inputSchema: object({
            use: { type: 'number', description: 'Port of the canvas session to work on from now on. Omit to just read.' },
            release: { type: 'boolean', description: 'True forgets a chosen session and goes back to picking automatically.' },
        }),
        run: async args => {
            const live = await sessions();

            if (args.release) {
                chosen = null;
                port = null;
            } else if (args.use !== undefined) {
                if (!live.some(one => one.port === args.use)) {
                    throw new Error(
                        `No canvas session on port ${args.use}. Live: ${live.map(one => one.port).join(', ') || 'none'}.`);
                }

                // Held for the rest of the process, so a choice survives the rediscovery that happens
                // whenever a call fails - otherwise the next hiccup would silently hand the agent back to
                // whichever session happens to be first.
                chosen = args.use;
                port = args.use;
            }

            // Resolved rather than reported blank: "which one am I on" should answer before the first
            // edit, not after it - and never name a port that has stopped answering, which is how a
            // dead session used to keep presenting itself as the live one.
            if (port === null || !live.some(one => one.port === port)) {
                await discover();
            }

            const rhinos = await rhinoSessions();

            if (rhinoPort === null || !rhinos.some(one => one.port === rhinoPort)) {
                await discoverRhino();
            }

            return {
                using: port,
                sessions: live,
                chosen,
                pinned: process.env.PHENOME_GH_PORT ?? null,
                rhino: { using: rhinoPort, sessions: rhinos },
            };
        },
    },
    {
        name: 'screenshot',
        description: 'The active Rhino viewport as an image - low resolution by default, on purpose, and framed on the geometry for the capture (the camera goes back where the human left it). Use to see what got built; for canvas layout, read canvas positions instead.',
        inputSchema: object({
            width: { type: 'number', description: 'Pixels across; default 640.' },
            zoomExtents: { type: 'boolean', description: "False captures the human's current framing instead." },
        }),
        run: async args => {
            const answer = await ask(`/screenshot?width=${args.width ?? 640}&zoomExtents=${args.zoomExtents ?? true}`);

            if (!answer.png) {
                return answer;
            }

            return { __image: answer.png };
        },
    },
    {
        name: 'plugins',
        description: "What is loaded: Grasshopper libraries and loaded Rhino plug-ins, each with its version and the file it came from. Read this when something in the console names a plug-in, or when a component behaves like a version other than the one you expect. Libraries marked shipped came with Grasshopper; the rest somebody installed.",
        inputSchema: object({}),
        run: () => ask('/plugins'),
    },
    {
        name: 'camera',
        description: "Read or aim the active Rhino viewport's camera. Called with no arguments it answers where the camera is: projection, location, target, up, 35mm lens length and the viewport's pixel size. Pass any of those to change only that. This is the way to frame a particular view - Rhino's Zoom is an interactive command, and scripting it with a magnification waits for a pick that never comes, which holds the UI thread and makes every other tool report that Rhino is busy.",
        inputSchema: object({
            location: { type: 'array', items: { type: 'number' }, description: 'Camera position [x,y,z].' },
            target: { type: 'array', items: { type: 'number' }, description: 'Point the camera looks at [x,y,z].' },
            up: { type: 'array', items: { type: 'number' }, description: 'Up direction [x,y,z].' },
            lens: { type: 'number', description: '35mm-equivalent lens length; larger is a narrower view.' },
            projection: { type: 'string', description: "'perspective' or 'parallel'." },
        }),
        run: async args => {
            const aiming = ['location', 'target', 'up', 'lens', 'projection']
                .some(key => args[key] !== undefined);

            return aiming ? ask('/camera', args) : ask('/camera');
        },
    },
    {
        name: 'canvas_image',
        description: "The Grasshopper canvas itself as an image, fitted to the whole document (the view is put back afterwards). This is how you see whether your layout reads - coordinates and lint findings are not the same as looking. Use it after arrange.",
        inputSchema: object({
            width: { type: 'number', description: 'Pixels across; default 1200.' },
            fit: { type: 'boolean', description: "False captures the human's current framing instead." },
        }),
        run: async args => {
            const answer = await ask(`/canvas-image?width=${args.width ?? 1200}&fit=${args.fit ?? true}`);

            return answer.png ? { __image: answer.png } : answer;
        },
    },
    {
        name: 'place',
        description: "A whole group's body in one call: objects with local ids, wired to each other, to the group's inlet and outlet ids, and to anything already on the canvas. Each object: {id?, name|guid, nickname?, pivot?:[x,y], slider?:{value,minimum,maximum,decimals}, text?, value?, inputs?:[{param?, sources?:[{id, output?}], value?}]} - an input takes 'sources' for wires OR 'value' for a constant typed straight into that socket, and 'param' is a name or an index. Pass 'group' and everything placed joins that group. Answers the local-id to canvas-id map. Always prefer this over add/wire loops. 'text' is the wording of a note and works on both a Scribble and a Panel; it is refused when empty rather than becoming a placeholder, and 'describe' reads it back with the note's position so you can check what you wrote without asking anybody to look at the screen.",
        inputSchema: object({
            objects: { type: 'array', items: { type: 'object' }, description: 'The recipe, in dataflow order.' },
            group: str("The group this body belongs to - everything placed joins it."),
        }, ['objects']),
        run: args => ask('/place', args),
    },
    {
        name: 'peek',
        description: "The full data on one parameter, branch by branch with tree paths - the numbers to verify a definition by, beyond the five-value sample in canvas. Pass a GROUP's id instead and it answers that group's signature as it stands: every inlet and outlet with its type, branch and item counts, and a few values off each outlet - a function's current type, in one call, without knowing its ports' ids first.",
        inputSchema: object({
            id: str('Object id.'),
            side: { type: 'string', enum: ['input', 'output'] },
            param: str('Parameter name or index; omit when there is only one.'),
        }, ['id']),
        run: args => ask(`/peek?id=${args.id}${args.side ? `&side=${args.side}` : ''}${args.param !== undefined ? `&param=${encodeURIComponent(args.param)}` : ''}`),
    },
    {
        name: 'save',
        description: "Save the Grasshopper document - to its own path, or to 'path' when it was never saved. Call it whenever you have finished editing: an unsaved canvas is the session's work resting on a running process. An autosave also runs once before your first edit, but that is a net, not a save.",
        inputSchema: object({ path: str('Absolute .gh path, when the document has none yet.') }),
        run: args => ask('/save', args),
    },
    {
        name: 'zoom',
        description: 'Focus the canvas view on those objects - use with select to direct the human somewhere.',
        inputSchema: object({ ids: ids('Object ids to frame.') }, ['ids']),
        run: args => ask('/zoom', args),
    },
    {
        name: 'rhino_command',
        description: "Run a Rhino command script - layers, blocks, groups, anything the command line speaks. Use the scripting dialect: a leading '-' suppresses dialogs, e.g. \"-_Layer New Walls Enter\".",
        inputSchema: object({ script: str('The command script.') }, ['script']),
        run: args => askRhino('/command', args),
    },
    {
        name: 'rhino_doc',
        description: 'The Rhino document: name, layers (with visibility and locks), object count.',
        inputSchema: object({}),
        run: () => askRhino('/doc'),
    },
];

// ----------------------------------------------------------------------------------------- instructions

/// The composition rules, handed over at initialize.
///
/// The only channel that does not depend on anybody reading anything: a client puts a server's
/// instructions into the model's context before the first tool call. AGENTS.md is a convention some agents
/// follow and others do not - Claude reads CLAUDE.md, others read neither - so the notes travel with the
/// tools instead. Read from the workspace's own AGENTS.md when it is there, so there is one source of
/// truth and this file cannot drift from it; the summary below is the fallback.
function instructions() {
    try {
        const notes = fs.readFileSync(path.join(process.cwd(), 'AGENTS.md'), 'utf8');
        const start = notes.indexOf('<!-- phenome-link:start -->');
        const end = notes.indexOf('<!-- phenome-link:end -->');

        if (start >= 0 && end > start) {
            return notes.slice(start, end).replace('<!-- phenome-link:start -->', '').trim();
        }
    } catch {
        // No workspace notes; the summary stands in.
    }

    return [
        'These tools drive a live Grasshopper canvas. The rules an author is held to:',
        '',
        '1. A group is a function and does exactly one thing; name every group for that thing.',
        '2. Declare each group with its signature first (`group` takes inlets and outlets and answers a',
        '   name-to-id map), then fill its body with one `place` call passing that group id.',
        '3. An object belongs to exactly ONE group. Sharing one makes `signature` refuse, for good reason.',
        '4. Batch: `wire` takes wires:[...], `set` takes values:[...]. Never one call per wire.',
        '5. Colour by role: blue [70,110,255] user inputs, grey [150,150,150] plain function,',
        '   red [255,60,60] geometry baked to Rhino, yellow [255,220,0] preview only.',
        '6. Sliders get real domains ("1000<2000<3000"); a constant goes in the socket via `set` with param.',
        '7. Never rename a component. Never use the simplify modifier. Flatten and graft as components.',
        '8. Data travels on wires, one value per wire - never packed into text.',
        '9. Two wires into one socket meet only where their paths agree: sources at different depths',
        '   ({0} and {0;0}) never share a branch. `peek` the input, not the output.',
        '10. Never position anything by hand: `arrange` lays groups out as blocks, and places notes by the',
        '    group they belong to - in a group it is that group\'s caption, in none it is the document title.',
        '11. A scribble is a comment, a group\'s name is the function signature. Do not write both saying the',
        '    same thing: a caption repeating the group name is a wasted line. Most groups need no caption.',
        '12. Finish with `signature` once, `arrange`, then `review` - fix every finding marked blocking,',
        '    treat polish as optional. Then `preview` with no id, which leaves only the outlets of the red',
        '    and yellow groups drawing and darkens the scaffolding - then `save`.',
        '',
        'Verify numerically with `peek` (branch and item counts are the specification) and look at your',
        'layout with `canvas_image`. When a tool fights you, say so with `report`.',
    ].join('\n');
}

// ---------------------------------------------------------------------------------------------- serving

function reply(id, result) {
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, result }) + '\n');
}

function complain(id, message) {
    process.stdout.write(JSON.stringify({ jsonrpc: '2.0', id, error: { code: -32000, message } }) + '\n');
}

let pending = '';
let queue = Promise.resolve();

process.stdin.on('data', chunk => {
    pending += chunk;

    let cut;

    while ((cut = pending.indexOf('\n')) >= 0) {
        const line = pending.slice(0, cut).trim();
        pending = pending.slice(cut + 1);

        if (line) {
            // One at a time, in arrival order: a launch must finish standing the session up before the
            // call behind it asks that session for anything.
            queue = queue.then(() => handle(line)).catch(() => {});
        }
    }
});

async function handle(line) {
    let message;

    try {
        message = JSON.parse(line);
    } catch {
        return;
    }

    const { id, method, params } = message;

    switch (method) {
        case 'initialize':
            reply(id, {
                protocolVersion: params?.protocolVersion ?? '2024-11-05',
                capabilities: { tools: {} },
                serverInfo: { name: 'grasshopper', version: '0.23.0' },
                instructions: instructions(),
            });
            break;

        case 'tools/list':
            reply(id, { tools: TOOLS.map(({ name, description, inputSchema }) => ({ name, description, inputSchema })) });
            break;

        case 'tools/call': {
            const tool = TOOLS.find(candidate => candidate.name === params?.name);

            if (!tool) {
                complain(id, `No tool called '${params?.name}'.`);
                break;
            }

            try {
                const answer = await tool.run(params?.arguments ?? {});

                if (answer && answer.__image) {
                    reply(id, { content: [{ type: 'image', data: answer.__image, mimeType: 'image/png' }] });
                    break;
                }

                const text = typeof answer === 'string' ? answer : JSON.stringify(answer, null, 2);

                reply(id, { content: [{ type: 'text', text }] });
            } catch (failed) {
                reply(id, { content: [{ type: 'text', text: failed.message }], isError: true });
            }

            break;
        }

        case 'ping':
            reply(id, {});
            break;

        default:
            // Notifications (initialized, cancelled) pass in silence; unknown requests answer honestly.
            if (id !== undefined) {
                complain(id, `Method '${method}' is not supported.`);
            }
    }
}
