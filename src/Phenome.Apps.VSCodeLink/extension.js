// The VS Code end of the Grasshopper link, and nothing else. A Rhino running the Phenome Link plugin
// writes %TEMP%\phenome-link-<pid>.port and answers loopback HTTP from then on; this extension keeps the
// human's window onto that: a status bar item saying whether a session exists, an output channel mirroring
// the journal, a command that drops the canvas recipe into the editor, and the script round-trip where
// saving a .gh.cs file pushes source back to the component that owns it.
//
// Deliberately independent of the Phenome configurator extension - the link is useful to any Grasshopper
// user with any agent, and this half mirrors that: it knows HTTP and the journal, not the kernel.

const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

let context = null;

const link = {
    port: null,
    cursor: 0,
    status: null,
    channel: null,
    timer: null,
    diagnostics: null,
    scriptsDir: null,
};

function linkLog(line) {
    link.channel ??= vscode.window.createOutputChannel('Phenome GH');
    link.channel.appendLine(line);
}

async function linkFetch(pathname, body) {
    const controller = new AbortController();
    const cut = setTimeout(() => controller.abort(), 3000);

    try {
        const answer = await fetch(`http://127.0.0.1:${link.port}${pathname}`, {
            method: body ? 'POST' : 'GET',
            body: body ? JSON.stringify({ author: 'vscode', ...body }) : undefined,
            signal: controller.signal,
        });

        return await answer.json();
    } finally {
        clearTimeout(cut);
    }
}

/// Finds a live session: every phenome-link-*.port file names a candidate; the first port that answers
/// GET / wins. Dead Rhinos leave stale files behind, which is exactly why answering is the test.
async function discoverLink() {
    let files = [];

    try {
        files = fs.readdirSync(os.tmpdir()).filter(f => /^phenome-link-\d+\.port$/.test(f));
    } catch {
        return null;
    }

    for (const file of files) {
        try {
            const port = parseInt(fs.readFileSync(path.join(os.tmpdir(), file), 'utf8').trim(), 10);
            link.port = port;

            const hello = await linkFetch('/');

            if (hello?.phenome === 'grasshopper-link') {
                return port;
            }
        } catch {
            // Stale file or foreign server; keep looking.
        }
    }

    link.port = null;
    return null;
}

function paintLinkStatus() {
    link.status ??= vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 90);
    link.status.text = link.port ? `$(plug) GH :${link.port}` : '$(debug-disconnect) GH offline';
    link.status.tooltip = link.port
        ? `Grasshopper link on port ${link.port}. The journal runs in the 'Phenome GH' output channel.`
        : 'No Grasshopper session. Start Rhino with the Phenome Link plugin.';
    link.status.show();
}

/// The heartbeat: journal entries into the output channel, connection state into the status bar. Polling,
/// not push, on purpose - the journal keeps everything, so a missed beat costs nothing.
async function pollLink() {
    if (link.port === null) {
        await discoverLink();

        if (link.port === null) {
            paintLinkStatus();
            return;
        }

        link.cursor = 0;
        linkLog(`— connected to Grasshopper on port ${link.port} —`);

        offerTeaching().catch(() => {});
    }

    try {
        const journal = await linkFetch(`/events?since=${link.cursor}`);

        for (const entry of journal.events ?? []) {
            const extras = Object.entries(entry)
                .filter(([key]) => !['seq', 'at', 'author', 'kind'].includes(key))
                .map(([key, value]) => `${key}=${typeof value === 'string' ? value : JSON.stringify(value)}`)
                .join(' ');

            linkLog(`${entry.at} ${entry.author.padEnd(8)} ${entry.kind}${extras ? '  ' + extras : ''}`);
        }

        link.cursor = journal.latest ?? link.cursor;
    } catch {
        linkLog('— lost the Grasshopper session —');
        link.port = null;
    }

    paintLinkStatus();
}

/// GET /canvas into the active editor - the Transcribe gesture without the trip to the canvas.
async function insertRecipe() {
    if (link.port === null) {
        vscode.window.showInformationMessage('Phenome Link: no Grasshopper session to read a recipe from.');
        return;
    }

    const canvas = await linkFetch('/canvas');
    const recipe = JSON.stringify(canvas, null, 2);
    const editor = vscode.window.activeTextEditor;

    if (editor) {
        await editor.edit(edit => edit.insert(editor.selection.active, recipe));
    } else {
        const doc = await vscode.workspace.openTextDocument({ content: recipe, language: 'json' });
        await vscode.window.showTextDocument(doc);
    }
}

// ------------------------------------------------------------------------------------ script round-trip

/// Pick a script component, get its source as a file; Ctrl+S sends it back. The component's id travels in
/// the filename, so the save handler knows the addressee without any registry.
async function editScript() {
    if (link.port === null) {
        vscode.window.showInformationMessage('Phenome Link: no Grasshopper session.');
        return;
    }

    const listing = await linkFetch('/scripts');

    if (!listing.scripts?.length) {
        vscode.window.showInformationMessage('Phenome Link: no script components on the canvas.');
        return;
    }

    const picked = await vscode.window.showQuickPick(
        listing.scripts.map(s => ({
            label: s.nickname || s.name,
            description: `${s.generation} · ${s.id}`,
            id: s.id,
        })),
        { placeHolder: 'Which script component?' });

    if (!picked) {
        return;
    }

    const script = await linkFetch(`/script?id=${picked.id}`);

    link.scriptsDir ??= path.join(context.globalStorageUri.fsPath, 'gh-scripts');
    fs.mkdirSync(link.scriptsDir, { recursive: true });

    const file = path.join(
        link.scriptsDir,
        `${picked.label.replace(/[^\w-]/g, '_')}.${picked.id}.gh.cs`);

    fs.writeFileSync(file, script.source, 'utf8');

    const doc = await vscode.workspace.openTextDocument(file);
    await vscode.window.showTextDocument(doc);

    vscode.window.setStatusBarMessage('Phenome Link: saving this file sends it back to Grasshopper.', 5000);
}

/// The way back: a saved .gh.cs whose name carries a component id goes over POST /script, and whatever the
/// component complains about lands on the file as squiggles - the balloon's words, in the margin.
async function pushSavedScript(document) {
    const match = /\.([0-9a-f-]{36})\.gh\.cs$/i.exec(document.fileName);

    if (!match || link.port === null) {
        return;
    }

    const answer = await linkFetch('/script', { id: match[1], source: document.getText() });

    link.diagnostics ??= vscode.languages.createDiagnosticCollection('phenome-gh');

    const complaints = [];

    for (const [messages, severity] of [
        [answer.errors ?? [], vscode.DiagnosticSeverity.Error],
        [answer.warnings ?? [], vscode.DiagnosticSeverity.Warning]]) {
        for (const message of messages) {
            // The component speaks Roslyn: "…text… [line:column]". No position pins to the first line.
            const position = /\[(\d+):(\d+)\]\s*$/.exec(message);
            const line = position ? Math.max(0, parseInt(position[1], 10) - 1) : 0;
            const column = position ? Math.max(0, parseInt(position[2], 10) - 1) : 0;

            complaints.push(new vscode.Diagnostic(
                new vscode.Range(line, column, line, column + 1),
                message.replace(/\s*\[\d+:\d+\]\s*$/, ''),
                severity));
        }
    }

    link.diagnostics.set(document.uri, complaints);

    vscode.window.setStatusBarMessage(
        complaints.length === 0
            ? 'Phenome Link: script accepted by Grasshopper.'
            : `Phenome Link: Grasshopper answered with ${complaints.length} problem(s).`,
        5000);
}

// ------------------------------------------------------------------------------------------ teach agents

// The knowledge an agent needs is small and stable; what varies is where agents look for it. The emerging
// answer across agent CLIs is a file in the workspace root - AGENTS.md by convention, CLAUDE.md for
// Claude - so that is where this writes. Marker-delimited and idempotent: re-teaching replaces the section
// instead of stacking copies, and nothing else in the file is touched.

const TEACH_START = '<!-- phenome-link:start -->';
const TEACH_END = '<!-- phenome-link:end -->';

const TEACHING = `${TEACH_START}
## Pairing with Grasshopper (Phenome Link)

**Prefer the MCP tools.** The \`grasshopper\` server registers one per verb and your session already
lists them with their arguments, so they are not repeated here. Four habits that list cannot teach you:
search \`components\` before \`add\` when unsure of a name; prefer \`place\` over add/wire loops; verify
with \`peek\`, not \`screenshot\`, since the canvas carries positions and needs no picture; and use
\`launch\` when there is no session rather than starting Rhino yourself. A tool missing from your session
means the session predates the current server - restart it instead of falling back to raw HTTP.

**The components you will reach for, with their exact input names** - so you need not search for them.
Anything unusual: ask \`components\`, and \`describe\` tells you a placed object's real parameters.

| what | component | inputs |
| --- | --- | --- |
| a knob | \`Number Slider\` | (set its domain with \`set\`) |
| count out a series | \`Series\` | Start, Step, Count |
| divide a span | \`Range\` | Domain, Steps |
| arithmetic | \`Addition\` \`Subtraction\` \`Multiplication\` \`Division\` | A, B |
| a point | \`Construct Point\` | X coordinate, Y coordinate, Z coordinate |
| take a point apart | \`Deconstruct Point\` | Point |
| a line | \`Line\` | Start Point, End Point |
| a line from a direction | \`Line SDL\` | Start, Direction, Length |
| a box | \`Box 2Pt\` | Point A, Point B, Base |
| a box about a centre | \`Center Box\` | Base, X, Y, Z |
| gather lists into one | \`Merge\` | Data 1, Data 2, … (zoom adds more) |
| move something | \`Move\` | Geometry, Motion |
| a vector | \`Unit X\` \`Unit Y\` \`Unit Z\` | Factor |
| change tree structure | \`Flatten Tree\` \`Graft Tree\` | Tree |
| pair every A with every B | \`Cross Reference\` | List (A), List (B) |
| colour the preview | \`Custom Preview\` | Geometry, Material |
| a colour | \`Colour Swatch\` | (set its value) |
| a note on the canvas | \`Panel\` | (set its text) |

**How to build.** Not by making a mess and tidying it - by declaring the shape first, the way code is
written:

1. **Draw the plan as a mermaid flowchart** before touching the canvas - one \`subgraph\` per group, named
   for the one thing it does, with the values flowing between them. That diagram *is* the group structure,
   so step 2 is transcribing it rather than inventing it, and the human can read your plan before a single
   component exists. Keep it in the chat, not on the canvas. And to read a definition you did not build,
   ask for the same shape back: \`canvas\` with \`as:'mermaid'\`.
2. **Declare every group with its signature** - one \`group\` call per step, with \`inlets\` and
   \`outlets\` named after the pseudocode. You get back a name-to-id map of ports, and the whole skeleton
   of the definition exists before a single component does.
3. **Fill each body**, one group at a time: \`place\` the components (wiring them onto that group's inlet
   ids and into its outlet ids in the same call), then one batched \`wire\` for the connections between
   groups - outlet to inlet, never component to component across a boundary.
4. **\`arrange\`, then \`review\`, then fix, then \`save\`.** Nothing is positioned by hand at any point, and
   nothing is left unsaved: when you have finished editing, save the definition. An unsaved canvas is a
   session's work resting on a running process.

**Order matters more than you would think.** Settle all the grouping and naming *first* - to rename or
recolour a group, call \`group\` again with its \`id\`, never ungroup-and-regroup - and only then call
\`signature\` once, followed by \`arrange\` and \`review\`. Repeated \`signature\` calls between regroupings
were how a canvas ended up with parallel chains sharing endpoints, where disconnecting a wire appeared to
do nothing and a later delete cut the live copy. It is idempotent now, but it is still a finishing move.
Keep group names to plain characters - write "and", not "&", and never an HTML entity.

**When something looks wrong structurally, look before you cut.** \`wires\` gives the whole connection
list; \`peek\` gives one parameter's data. \`delete\` refuses when it would cut wires to objects that stay
and names them - read that list instead of forcing it. And \`undo\` exists: a delete or an arrange can be
taken back, one step at a time.

**Composition, the rules the review enforces.** A canvas is read group by group, so the groups ARE the
abstraction layer.

1. **A group is a function, and does exactly one thing.** Single responsibility taken literally: if the
   name needs an "and", a comma or a slash, it is two groups - "halve the dimensions" and "compute the
   leg height" are two functions, not one "halves and heights". Name every group for the one thing it
   does. Moderate size means up to about thirty objects; past that, split it.
2. **A group is a virtual component: give it a signature.** Call \`signature\` on each group right after
   grouping. It plants named floating parameters just inside the left edge as inlets and at the right edge
   as outlets, and re-lands every crossing wire on them. Nothing crosses a boundary except through them -
   wiring one group's component straight into another's is reaching into a function body.
3. **Never rename a component.** A component's nickname is how everyone recognises it; renaming
   Multiplication to "W/2" makes the canvas unreadable for the next person and for you. Names belong on
   the floating parameters - that is what they are for. Only parameters get nicknames.
4. **Never position groups by hand; call \`arrange\`.** It lays groups out as whole blocks, the only way
   frames stay apart: a layout that places components individually interleaves the members of different
   groups, and interleaved members force their frames to overlap. Nest one level at most; a mother group
   sits at the very back of the draw order (\`group\` and \`arrange\` see to that).
5. **Colour by role** - four roles, no others (drawn at quarter opacity, so give full colours):
   **blue** \`[70,110,255]\` inputs the user may modify - the sliders themselves, and nothing more: do not
   duplicate a parameter to have a second copy of it lying around, because a copy that feeds nothing is
   just a thing the next reader has to check before ignoring. Do **not** collect every input into one bank
   either: a blue group belongs where its knobs are used, beside the function they feed, so a reader finds
   a knob where its effect is; **red** \`[255,60,60]\` the components whose geometry gets baked into
   Rhino as the product - select the group, bake, and everything needed is there; **yellow**
   \`[255,220,0]\` preview-only geometry, never baked; **grey** \`[150,150,150]\` a plain function. Flow
   left to right.
6. **Sliders deserve real domains.** \`set\` takes \`minimum\`/\`maximum\`/\`decimals\`, or a string value
   like \`"0<1400<2400"\` setting the whole domain at once - a bare 0..1 slider is almost always wrong. A
   constant belongs in the socket that uses it: \`set\` with \`param\`, not a parameter and a wire.
7. **Respect the data tree; do not flatten your way out of trouble.** Grasshopper data is a tree of
   branches with paths like \`{0;1}\`, and a component runs once per item in the *longest* input, reusing
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
     per-unit values into N branches (\`Merge\` the sliders, then \`Graft Tree\` - one branch per unit).
     Per-unit arithmetic then broadcasts by itself. Where a list that is the *same for every unit* (the M
     shelf heights, the rung positions) has to meet the per-unit branches, plain matching will not cross
     two differently-shaped trees: use **\`Cross Reference\`**, which pairs every A with every B and gives
     you M items inside each unit's branch. Getting this wrong is what silently produces 1806 of something.
   - **Verify with \`peek\`** after each group: branch count and item counts are the specification.
     \`review\` flags an item-access input holding several items in a branch, which is this failure
     exactly - and it reports every red or orange component, so bringing review to zero means the
     definition actually runs.
8. **Build with components, not script.** A definition is made of components - that is what makes it
   readable and editable by whoever opens it next. Reach for a C# script component only when no
   combination of components can do the job, and say why when you do.
9. **Leave no dead ends.** Every component and parameter either feeds something or draws something. A
   parameter left over from a rethink, a component whose output went elsewhere - each is a thing the next
   reader must check before ignoring. \`review\` lists them as "unused"; delete them.
10. **When a tool fights you, say so.** Call \`report\` with what you expected against what happened -
   refused requests log themselves, this is for the rest. If the human hits repeated trouble, **ask them**
   whether to prepare a report they can mail (\`feedback\` assembles it and returns a mailto); never send
   anything yourself, and never call \`feedback\` without asking first.
11. **Finish with \`arrange\`, then \`review\`, and fix what review says.** It measures what can be
   measured - overlapping frames, unnamed groups, names confessing two jobs, oversized groups, renamed
   components, bare boundary crossings, ungrouped objects - so a definition converges instead of being
   hoped over. Leave notes in panels where a reader will need them.

If the tools are ever unavailable, the same protocol is plain HTTP: the port is in
\`%TEMP%\\phenome-link-<pid>.port\` (one file per Rhino, a stale one has a dead pid) and \`GET /\`
describes every verb. Put your own name in \`author\` on every POST and skip your own echo. The rest -
the journal's cursor, its gaps, the rules the verbs share - is in the plugin's \`docs/protocol.md\`.
${TEACH_END}`;

/// Writes the pairing knowledge into the workspace's agent files. AGENTS.md is created if absent;
/// CLAUDE.md is only updated when it already exists - creating it is the owner's call, not a plugin's.
async function teachAgents(quiet) {
    const folder = vscode.workspace.workspaceFolders?.[0];

    if (!folder) {
        if (!quiet) {
            vscode.window.showInformationMessage('Phenome Link: open a folder first - the notes live in the workspace.');
        }

        return;
    }

    const taught = [];

    // AGENTS.md carries the notes; CLAUDE.md gets them too, because that is the file Claude reads and a
    // fresh workspace has neither. Where CLAUDE.md has to be created, it only imports the notes rather
    // than copying them - two copies of a doctrine is one too many.
    const agents = path.join(folder.uri.fsPath, 'AGENTS.md');
    const claude = path.join(folder.uri.fsPath, 'CLAUDE.md');

    if (!fs.existsSync(claude)) {
        fs.writeFileSync(
            claude,
            '# Working in this workspace\n\nThe Grasshopper pairing notes live beside this file:\n\n'
                + '@AGENTS.md\n',
            'utf8');

        taught.push('CLAUDE.md');
    }

    for (const name of ['AGENTS.md', 'CLAUDE.md']) {
        const file = path.join(folder.uri.fsPath, name);
        const exists = fs.existsSync(file);

        // An existing CLAUDE.md gets the notes in full; one we just wrote already imports them.
        if (name === 'CLAUDE.md' && (!exists || fs.readFileSync(file, 'utf8').includes('@AGENTS.md'))) {
            continue;
        }

        let text = exists ? fs.readFileSync(file, 'utf8') : '';

        const start = text.indexOf(TEACH_START);
        const end = text.indexOf(TEACH_END);

        text = start >= 0 && end > start
            ? text.slice(0, start) + TEACHING + text.slice(end + TEACH_END.length)
            : (text.trimEnd() + '\n\n' + TEACHING + '\n').trimStart();

        fs.writeFileSync(file, text, 'utf8');
        taught.push(name);
    }

    // The MCP half: the server script into the workspace (stable path, survives extension updates), and
    // its registration merged into .mcp.json - which is where agents look for project tool servers.
    const home = path.join(folder.uri.fsPath, '.phenome');

    fs.mkdirSync(home, { recursive: true });
    fs.copyFileSync(path.join(context.extensionUri.fsPath, 'mcp.js'), path.join(home, 'gh-mcp.js'));

    const registry = path.join(folder.uri.fsPath, '.mcp.json');

    let servers = {};

    try {
        servers = JSON.parse(fs.readFileSync(registry, 'utf8'));
    } catch {
        // Absent or broken; either way this write is the whole content.
    }

    servers.mcpServers = {
        ...servers.mcpServers,
        grasshopper: { command: 'node', args: ['.phenome/gh-mcp.js'] },
    };

    fs.writeFileSync(registry, JSON.stringify(servers, null, 2) + '\n', 'utf8');
    taught.push('.mcp.json');

    // And the permissions, so the first session already trusts the server and its tools: one rule names
    // the whole server. Local settings, merged - whatever else lives there is somebody's and stays.
    const claudeDir = path.join(folder.uri.fsPath, '.claude');
    const local = path.join(claudeDir, 'settings.local.json');

    fs.mkdirSync(claudeDir, { recursive: true });

    let settings = {};

    try {
        settings = JSON.parse(fs.readFileSync(local, 'utf8'));
    } catch {
        // Absent or broken; either way this write is the whole content.
    }

    settings.enableAllProjectMcpServers = true;
    settings.permissions ??= {};
    settings.permissions.allow ??= [];

    if (!settings.permissions.allow.includes('mcp__grasshopper')) {
        settings.permissions.allow.push('mcp__grasshopper');
    }

    fs.writeFileSync(local, JSON.stringify(settings, null, 2) + '\n', 'utf8');
    taught.push('.claude/settings.local.json');

    if (!quiet) {
        vscode.window.showInformationMessage(`Phenome Link: pairing notes written to ${taught.join(', ')}.`);
    }
}

/// Offered once per workspace, when a live session first appears: the moment the knowledge becomes useful.
async function offerTeaching() {
    const folder = vscode.workspace.workspaceFolders?.[0];

    if (!folder || context.workspaceState.get('phenomeLink.teachingOffered')) {
        return;
    }

    await context.workspaceState.update('phenomeLink.teachingOffered', true);

    const agents = path.join(folder.uri.fsPath, 'AGENTS.md');

    if (fs.existsSync(agents) && fs.readFileSync(agents, 'utf8').includes(TEACH_START)) {
        return;
    }

    const answer = await vscode.window.showInformationMessage(
        'Grasshopper is live. Teach the agents in this workspace how to pair with it (AGENTS.md)?',
        'Teach', 'Not here');

    if (answer === 'Teach') {
        await teachAgents(false);
    }
}

// --------------------------------------------------------------------------------------------- feedback

/// Assembles the report, shows it, and offers a mail draft - the human reads it before anything moves, and
/// the sending is theirs. This side never posts anything outward.
async function reportProblem() {
    if (link.port === null) {
        vscode.window.showInformationMessage('Phenome Link: no Grasshopper session to report on.');
        return;
    }

    const expected = await vscode.window.showInputBox({
        prompt: 'What did you expect to happen?',
        ignoreFocusOut: true,
    });

    if (!expected) {
        return;
    }

    const got = await vscode.window.showInputBox({
        prompt: 'What happened instead?',
        ignoreFocusOut: true,
    });

    if (!got) {
        return;
    }

    const to = vscode.workspace.getConfiguration('phenomeLink').get('reportTo') || undefined;
    const draft = await linkFetch('/feedback', { expected, got, to });

    if (!draft.path) {
        vscode.window.showWarningMessage(`Phenome Link: ${draft.error ?? 'the report could not be assembled.'}`);
        return;
    }

    const document = await vscode.workspace.openTextDocument(draft.path);

    await vscode.window.showTextDocument(document);

    const answer = await vscode.window.showInformationMessage(
        'Report ready. Nothing has been sent - open a mail draft with it?',
        'Open mail draft', 'Show the file', 'Not now');

    if (answer === 'Open mail draft') {
        await vscode.env.openExternal(vscode.Uri.parse(draft.mailto));
        vscode.window.showInformationMessage('Attach the report file, read it over, then send it.');
    } else if (answer === 'Show the file') {
        await vscode.commands.executeCommand('revealFileInOS', vscode.Uri.file(draft.path));
    }
}

// ---------------------------------------------------------------------------------------------- pairing

/// The command that starts an agent. An explicit setting wins; the default 'claude' is honoured only if
/// PATH can resolve it; failing that, the newest Claude Code extension's own bundled CLI stands in.
function agentCommand() {
    const configured = vscode.workspace.getConfiguration('phenomeLink').get('agentCommand') || 'claude';

    if (configured !== 'claude') {
        return configured;
    }

    const onPath = (process.env.PATH ?? '').split(path.delimiter).some(dir => {
        try {
            return dir && ['claude.cmd', 'claude.exe', 'claude'].some(name => fs.existsSync(path.join(dir, name)));
        } catch {
            return false;
        }
    });

    if (onPath) {
        return 'claude';
    }

    try {
        const extensions = path.join(os.homedir(), '.vscode', 'extensions');
        const bundled = fs.readdirSync(extensions)
            .filter(name => /^anthropic\.claude-code-/.test(name))
            .sort()
            .reverse()
            .map(name => path.join(extensions, name, 'resources', 'native-binary', 'claude.exe'))
            .find(candidate => fs.existsSync(candidate));

        if (bundled) {
            return bundled;
        }
    } catch {
        // No extensions folder is a fine answer; the default speaks for itself below.
    }

    return 'claude';
}

/// The button on the canvas lands here: vscode://phenome.phenome-link/pair?port=NNNN opens (or wakes) this
/// window and starts an agent session with the handshake already typed. The port travels in the link, so
/// discovery is instant even before the poll finds the file.
function handleUri(uri) {
    if (uri.path !== '/pair') {
        return;
    }

    const port = new URLSearchParams(uri.query).get('port');

    if (port) {
        link.port = parseInt(port, 10);
        link.cursor = 0;
        paintLinkStatus();
    }

    // The handshake is deliberately self-contained: the URI lands in whichever window was focused last,
    // and that window's workspace - if there is one at all - need not know Phenome. Everything the agent
    // must learn, the server itself teaches: GET / is the protocol.
    const where = port
        ? `http://127.0.0.1:${port}`
        : 'the port in %TEMP%\\phenome-link-*.port (a stale file has a dead pid)';

    const agent = agentCommand();
    const invoke = /[\\/]/.test(agent) ? `& '${agent.replace(/'/g, "''")}'` : agent;

    // The port travels into the session's environment, so the agent's MCP server binds to the canvas whose
    // button was pressed rather than to whichever session happens to answer first. With several Rhinos up -
    // one per agent - that is the difference between pairing and gatecrashing.
    const terminal = vscode.window.createTerminal({
        name: port ? `Claude × Grasshopper :${port}` : 'Claude × Grasshopper',
        env: port ? { PHENOME_GH_PORT: String(port) } : undefined,
    });

    terminal.sendText(
        `${invoke} "You are pairing with a live Grasshopper canvas${port ? ` on port ${port}` : ''}. If you ` +
        `have 'grasshopper' MCP tools (canvas, events, say, ...), use them - each asks permission once, and ` +
        `they are already bound to this canvas${port ? '' : ' by discovery'}. Otherwise it is loopback HTTP ` +
        `at ${where}: GET / describes the whole protocol - start there. Read the canvas, then greet the ` +
        `human with say (author 'claude'). Poll events?since=N while pairing (the response's 'latest' is ` +
        `your next cursor); entries carry author - skip your own echo. The human's messages arrive as ` +
        `kind:'message' entries."`);
    terminal.show();
}

// ---------------------------------------------------------------------------------------------- activate

function activate(extensionContext) {
    context = extensionContext;

    context.subscriptions.push(
        vscode.commands.registerCommand('phenomeLink.insertRecipe', () => insertRecipe()),
        vscode.commands.registerCommand('phenomeLink.editScript', () => editScript()),
        vscode.commands.registerCommand('phenomeLink.teachAgents', () => teachAgents(false)),
        vscode.commands.registerCommand('phenomeLink.reportProblem', () => reportProblem()),

        vscode.window.registerUriHandler({ handleUri }),

        vscode.workspace.onDidSaveTextDocument(document => {
            pushSavedScript(document).catch(failed => linkLog(`script push failed: ${failed.message}`));
        }));

    // The Grasshopper heartbeat: cheap when connected, cheaper when not.
    paintLinkStatus();
    link.timer = setInterval(() => { pollLink().catch(() => {}); }, 2500);
}

function deactivate() {
    if (link.timer) {
        clearInterval(link.timer);
    }
}

module.exports = { activate, deactivate };
