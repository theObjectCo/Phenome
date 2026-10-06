// This file is the VS Code half of the Grasshopper link. A Rhino running the Phenome Link plugin writes
// %TEMP%\phenome-link-<pid>.port and answers loopback HTTP from then on. This extension adds:
// - a status bar item showing whether a session exists, and a canvas switcher behind it,
// - an output channel mirroring the journal,
// - a command that inserts the canvas recipe into the editor,
// - the script round-trip: saving a .gh.cs file pushes its source back to the owning component,
// - two panels showing the canvas and the Rhino viewport,
// - Teach Agents, which writes the pairing notes and the MCP registration into a workspace,
// - Report a Problem, which assembles a report and offers a mail draft,
// - the pair handler behind the canvas's Pair with VS Code button.
//
// It is independent of the Phenome configurator extension by design: this half uses HTTP and the journal
// only, and never the kernel.

const vscode = require('vscode');
const fs = require('fs');
const os = require('os');
const path = require('path');

let context = null;

const link = {
    port: null,

    /// Port pinned by an explicit canvas choice; discovery must not replace it. Null means "first that answers".
    pinned: null,

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

            // Required since 0.32.0. The link demands this header because a page cannot set it on a no-cors
            // request.
            headers: body ? { 'Content-Type': 'application/json', 'X-Phenome-Client': 'vscode' } : undefined,
            body: body ? JSON.stringify({ author: 'vscode', ...body }) : undefined,
            signal: controller.signal,
        });

        return await answer.json();
    } finally {
        clearTimeout(cut);
    }
}

/// Find a live session: every phenome-link-*.port file is a candidate and the first to answer GET / is used.
/// Dead Rhinos leave stale files behind, and an answer is the only reliable test.
async function discoverLink() {
    // A pinned port outranks discovery and is checked before use. The Rhino it names may have closed, and a
    // stale pin would make every later call report a session that does not exist.
    if (link.pinned !== null) {
        try {
            link.port = link.pinned;

            if ((await linkFetch('/'))?.phenome === 'grasshopper-link') {
                return link.pinned;
            }
        } catch {
            // Gone; fall through and drop the pin below.
        }

        linkLog(`— the canvas on port ${link.pinned} stopped answering; discovering again —`);
        link.pinned = null;
    }

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

/// Two views of the same machine, one per panel, refreshed on request.
///
/// Kept separate so the panel being watched keeps the screen. Refresh is on demand by design. Both verbs run
/// on Rhino's single UI thread, and an auto-refreshing panel would take time from the person using that
/// machine.
const podglad = { canvas: null, viewport: null };

async function showPicture(kind) {
    const isCanvas = kind === 'canvas';
    const title = isCanvas ? 'Grasshopper canvas' : 'Rhino viewport';
    const id = isCanvas ? 'phenomeCanvas' : 'phenomeViewport';

    if (podglad[isCanvas ? 'canvas' : 'viewport']) {
        podglad[isCanvas ? 'canvas' : 'viewport'].reveal();
    } else {
        const panel = vscode.window.createWebviewPanel(
            id, title, vscode.ViewColumn.Beside, { enableScripts: true, retainContextWhenHidden: true });

        panel.onDidDispose(() => { podglad[isCanvas ? 'canvas' : 'viewport'] = null; });
        panel.webview.onDidReceiveMessage(message => {
            if (message?.what === 'refresh') {
                refreshPicture(kind).catch(failed => linkLog(`${title}: ${failed.message}`));
            }
        });

        podglad[isCanvas ? 'canvas' : 'viewport'] = panel;
        panel.webview.html = pictureHtml(title, null, null);
    }

    await refreshPicture(kind);
}

async function refreshPicture(kind) {
    const isCanvas = kind === 'canvas';
    const panel = podglad[isCanvas ? 'canvas' : 'viewport'];

    if (!panel) {
        return;
    }

    const title = isCanvas ? 'Grasshopper canvas' : 'Rhino viewport';

    if (link.port === null) {
        panel.webview.html = pictureHtml(title, null, 'No Grasshopper session is answering.');
        return;
    }

    try {
        // The canvas half serves both verbs and both take a width. Neither depends on the Rhino plug-in being
        // registered, which is the fragile part.
        const answer = await linkFetch(isCanvas ? '/canvas-image?width=1400&save=false' : '/screenshot?width=1400&save=false');

        if (!answer?.png) {
            panel.webview.html = pictureHtml(title, null, answer?.error ?? 'No image was returned.');
            return;
        }

        panel.webview.html = pictureHtml(title, answer.png, null);
    } catch (failed) {
        panel.webview.html = pictureHtml(title, null, failed.message);
    }
}

function pictureHtml(title, png, trouble) {
    const stamp = new Date().toLocaleTimeString();

    // HTML-escaped: trouble is raw server or fetch text, and an unescaped < would hide the message that
    // explains why there is no picture.
    const plain = (trouble ?? 'No image yet. Press Refresh.')
        .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

    const body = png
        ? `<img src="data:image/png;base64,${png}" alt="${title}">`
        : `<p class="trouble">${plain}</p>`;

    return `<!DOCTYPE html>
<html><head><meta charset="utf-8"><style>
  body { margin: 0; padding: 0; background: var(--vscode-editor-background);
         color: var(--vscode-foreground); font-family: var(--vscode-font-family); }
  header { display: flex; align-items: center; gap: 1rem; padding: .5rem .75rem;
           border-bottom: 1px solid var(--vscode-panel-border); }
  h1 { font-size: .9rem; font-weight: 600; margin: 0; flex: 1; }
  time { opacity: .6; font-size: .8rem; }
  button { font: inherit; padding: .25rem .9rem; cursor: pointer;
           color: var(--vscode-button-foreground); background: var(--vscode-button-background);
           border: none; border-radius: 2px; }
  img { display: block; width: 100%; height: auto; }
  .trouble { padding: 2rem .75rem; opacity: .7; }
</style></head><body>
  <header><h1>${title}</h1><time>${png ? stamp : ''}</time>
    <button id="again">Refresh</button></header>
  ${body}
  <script>
    // Acquired once and kept. acquireVsCodeApi throws on the second call in the same document, so calling
    // it from the click handler would work exactly once per refresh and then stop.
    const editor = acquireVsCodeApi();
    document.getElementById('again').addEventListener(
        'click', () => editor.postMessage({ what: 'refresh' }));
  </script>
</body></html>`;
}

/// Every canvas that answers, newest first, with enough detail to tell them apart.
///
/// discoverLink takes the first reply, which is fine for one Rhino and arbitrary for several, because
/// readdirSync promises no order. This list makes the choice explicit and works from a remote session; the
/// button on the canvas reaches only the editor beside Rhino.
async function listSessions() {
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
    const wasPort = link.port;

    for (const file of files) {
        try {
            const port = parseInt(fs.readFileSync(file, 'utf8').trim(), 10);
            link.port = port;

            const hello = await linkFetch('/');

            if (hello?.phenome !== 'grasshopper-link') {
                continue;
            }

            // The document name is what distinguishes two canvases for a user; the pid distinguishes them
            // for everything else. It is asked after the greeting: a Rhino that is alive but busy is still
            // listed and not skipped for being slow.
            let name = null;

            try {
                name = (await linkFetch('/canvas'))?.document?.name ?? null;
            } catch {
                // Busy or mid-solve: still a session, though its document name cannot be read now.
            }

            live.push({
                port,
                pid: parseInt(/-(\d+)\.port$/.exec(path.basename(file))?.[1] ?? '0', 10),
                name,
            });
        } catch {
            // Stale file or foreign server; keep looking.
        }
    }

    link.port = wasPort;
    return live;
}

/// Ask which canvas and remember the choice.
///
/// The choice is pinned. pollLink re-discovers after any failed call and without a pin would fall back to the
/// first session that answers. The pin is dropped when that Rhino stops answering and does not outlive the
/// canvas it named, which is the same rule the MCP server follows.
async function switchCanvas() {
    const live = await listSessions();

    if (live.length === 0) {
        vscode.window.showInformationMessage(
            'No Grasshopper session is answering. Start Rhino with Grasshopper open.');
        return;
    }

    const items = live.map(one => ({
        label: one.name ? `$(circuit-board) ${one.name}` : '$(circuit-board) unsaved',
        description: `port ${one.port} · Rhino ${one.pid}`,
        detail: one.port === link.port ? 'currently connected' : undefined,
        port: one.port,
    }));

    const picked = await vscode.window.showQuickPick(items, {
        title: 'Which canvas?',
        placeHolder: live.length === 1 ? 'One session is running' : `${live.length} sessions are running`,
    });

    if (!picked) {
        return;
    }

    link.port = picked.port;
    link.pinned = picked.port;
    link.cursor = 0;
    paintLinkStatus();
    linkLog(`— switched to the canvas on port ${picked.port} —`);

    // The canvas button uses the same mechanism. The agent's MCP server binds to the canvas that was chosen
    // instead of to the first session that answers.
    process.env.PHENOME_GH_PORT = String(picked.port);
}

function paintLinkStatus() {
    link.status ??= vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 90);
    const pin = link.pinned !== null && link.pinned === link.port ? '$(pinned) ' : '';

    link.status.text = link.port ? `${pin}$(plug) GH :${link.port}` : '$(debug-disconnect) GH offline';
    link.status.tooltip = link.port
        ? `Grasshopper link on port ${link.port}${link.pinned === link.port ? ', manually selected' : ''}.`
          + " Click to switch canvas. The journal runs in the 'Phenome GH' output channel."
        : 'No Grasshopper session. Start Rhino with the Phenome Link plugin. Click to rediscover.';

    // The item is clickable. It is where the current attachment is read, and a click offers a choice instead
    // of only reporting what discovery happened to find.
    link.status.command = 'phenomeLink.switchCanvas';
    link.status.show();
}

/// Heartbeat: journal entries go to the output channel and the connection state to the status bar. The
/// journal is polled, not pushed, because it is complete and a missed poll loses nothing.
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

/// Insert GET /canvas into the active editor: the canvas recipe without going to the canvas.
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

/// Pick a script component and open its source as a file; Ctrl+S sends it back. The component id is part of
/// the filename, and the save handler reads the target from it without a registry.
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

/// Return path: saving a .gh.cs whose filename carries a component id posts it to /script, and the
/// component's error and warning messages from the recomputation become diagnostics on the file.
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
            // Roslyn messages read "<text> [line:column]". A message with no position is placed on the first line.
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

// The notes are small and change rarely; the place agents read them varies. The convention across agent CLIs
// is a file in the workspace root (AGENTS.md, and CLAUDE.md for Claude), and this writes there. The section is
// delimited by the markers below and replaced on every run. Repeated calls do not stack copies, and the rest
// of the file is left untouched.

const TEACH_START = '<!-- phenome-link:start -->';
const TEACH_END = '<!-- phenome-link:end -->';

/// The notes live in notes/pairing.md and ship with the extension.
///
/// As a markdown file the text needs no escaped backticks and can be edited without touching JavaScript.
/// Changing what an agent is told is a content change and not a code change.
function notes() {
    return fs.readFileSync(path.join(context.extensionUri.fsPath, 'notes', 'pairing.md'), 'utf8').trim();
}

const TEACHING = () => `${TEACH_START}
${notes()}
${TEACH_END}`;

/// Write the pairing notes into the workspace's agent files. AGENTS.md is created if absent and carries the
/// notes. A CLAUDE.md that already exists gets them in full; one that does not exist is created as a
/// reference to AGENTS.md rather than a second copy.
async function teachAgents(quiet) {
    const folder = vscode.workspace.workspaceFolders?.[0];

    if (!folder) {
        if (!quiet) {
            vscode.window.showInformationMessage('Phenome Link: open a folder first. The notes are written into the workspace.');
        }

        return;
    }

    const taught = [];

    // AGENTS.md carries the notes and CLAUDE.md gets them too, because that is the file Claude reads and a
    // new workspace has neither. A CLAUDE.md that has to be created imports AGENTS.md instead of holding a
    // second copy of the same text.
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

        // An existing CLAUDE.md gets the notes in full; one written just above already imports them.
        if (name === 'CLAUDE.md' && (!exists || fs.readFileSync(file, 'utf8').includes('@AGENTS.md'))) {
            continue;
        }

        let text = exists ? fs.readFileSync(file, 'utf8') : '';

        const start = text.indexOf(TEACH_START);
        const end = text.indexOf(TEACH_END);

        text = start >= 0 && end > start
            ? text.slice(0, start) + TEACHING() + text.slice(end + TEACH_END.length)
            : (text.trimEnd() + '\n\n' + TEACHING() + '\n').trimStart();

        fs.writeFileSync(file, text, 'utf8');
        taught.push(name);
    }

    // MCP half: copy the server script to a stable workspace path that survives extension updates, then merge
    // its registration into every location a host reads for project tool servers.
    const home = path.join(folder.uri.fsPath, '.phenome');

    fs.mkdirSync(home, { recursive: true });
    fs.copyFileSync(path.join(context.extensionUri.fsPath, 'mcp.js'), path.join(home, 'gh-mcp.js'));

    // Each host reads its own registration file and none of the others; .mcp.json is where Claude Code looks.
    // An agent on a host with no registration sees none of this server's tools, while the notes it has just
    // been given describe the work almost entirely in terms of those tools.
    //
    // The server is registered in all of them, merged into what is there: these files belong to the workspace
    // owner and may already list servers of their own. A config for a host that is not installed costs one
    // unread file; a missing config for the host that is installed loses the feature.
    //
    // The path stays relative. All of these hosts start the server with the workspace as the working
    // directory, and these files are committed; an absolute path would encode one machine in a shared file.
    const server = { command: 'node', args: ['.phenome/gh-mcp.js'] };

    // VS Code's own MCP support uses `servers` instead of `mcpServers` and silently ignores the other key. A
    // wrong spelling there looks like a broken feature.
    const registries = [
        { file: '.mcp.json', key: 'mcpServers' },            // Claude Code
        { file: '.kilocode/mcp.json', key: 'mcpServers' },   // Kilo Code
        { file: '.roo/mcp.json', key: 'mcpServers' },        // Roo Code
        { file: '.cursor/mcp.json', key: 'mcpServers' },     // Cursor
        { file: '.vscode/mcp.json', key: 'servers' },        // VS Code, Copilot
    ];

    for (const { file, key } of registries) {
        const registry = path.join(folder.uri.fsPath, file);

        let servers = {};

        try {
            servers = JSON.parse(fs.readFileSync(registry, 'utf8'));
        } catch {
            // Absent or broken; either way this write is the whole content.
        }

        // This key becomes the host's tool prefix (`mcp__phenome__*`); the name the server reports in its
        // handshake does not. The key was `grasshopper` before 0.30.0. The server drives Rhino as much as
        // Grasshopper, and half its verbs answer with no canvas open.
        //
        // The old key is deleted and not left beside the new one: a host that finds both registers two
        // servers, starts two copies of this script and exposes every verb twice under two prefixes.
        servers[key] = { ...servers[key], phenome: server };
        delete servers[key].grasshopper;

        fs.mkdirSync(path.dirname(registry), { recursive: true });
        fs.writeFileSync(registry, JSON.stringify(servers, null, 2) + '\n', 'utf8');
        taught.push(file);
    }

    // Permissions: one rule covers the whole server, and the first session already trusts the server and its
    // tools. settings.local.json is merged into, and its existing entries stay.
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

    if (!settings.permissions.allow.includes('mcp__phenome')) {
        settings.permissions.allow.push('mcp__phenome');
    }

    // The rule for the old name is removed for the same reason as the old registration key: it names a server
    // this workspace no longer has, and a stale allow rule is one more line to investigate later.
    settings.permissions.allow = settings.permissions.allow.filter(rule => rule !== 'mcp__grasshopper');

    fs.writeFileSync(local, JSON.stringify(settings, null, 2) + '\n', 'utf8');
    taught.push('.claude/settings.local.json');

    if (!quiet) {
        vscode.window.showInformationMessage(`Phenome Link: pairing notes written to ${taught.join(', ')}.`);
    }
}

/// Brings a taught workspace up to this version of the extension, at startup.
///
/// A workspace taught by an older extension keeps its copy of the MCP server and its notes until somebody runs
/// Teach Agents again, and a stale `.phenome/gh-mcp.js` fails in ways that point nowhere near the cause: a
/// 0.31.0 copy sent no content type, and every POST was refused while every GET still worked. This compares the
/// copy and the notes section with what this extension carries and, when either differs, teaches the workspace
/// again and says so once. A workspace that was never taught has no `.phenome/gh-mcp.js` and is left alone.
/// The setting `phenomeLink.updateTaughtWorkspaces` turns it off.
async function refreshTeaching() {
    const folder = vscode.workspace.workspaceFolders?.[0];

    if (!folder || !vscode.workspace.getConfiguration('phenomeLink').get('updateTaughtWorkspaces', true)) {
        return;
    }

    const copy = path.join(folder.uri.fsPath, '.phenome', 'gh-mcp.js');

    if (!fs.existsSync(copy)) {
        return;
    }

    // Line endings are ignored: a workspace that commits its copy gets CRLF back from git on Windows, and a
    // difference in line endings alone would update the workspace and announce it on every start.
    const same = (one, other) => one.split('\r\n').join('\n') === other.split('\r\n').join('\n');

    const bundled = fs.readFileSync(path.join(context.extensionUri.fsPath, 'mcp.js'), 'utf8');
    const serverStale = !same(fs.readFileSync(copy, 'utf8'), bundled);

    // The notes are compared where they are held in full. A CLAUDE.md that imports AGENTS.md holds none.
    const notesStale = ['AGENTS.md', 'CLAUDE.md'].some(name => {
        const file = path.join(folder.uri.fsPath, name);

        if (!fs.existsSync(file)) {
            return false;
        }

        const text = fs.readFileSync(file, 'utf8');
        const start = text.indexOf(TEACH_START);
        const end = text.indexOf(TEACH_END);

        return start >= 0 && end > start && !same(text.slice(start, end + TEACH_END.length), TEACHING());
    });

    if (!serverStale && !notesStale) {
        return;
    }

    await teachAgents(true);

    const version = context.extension?.packageJSON?.version ?? 'this version';

    linkLog(`updated the agent files in ${folder.name} to ${version}`);
    vscode.window.showInformationMessage(
        `Phenome Link: updated the agent files in this workspace to ${version}. An agent session that is `
            + 'already running keeps the old MCP server until it reconnects (/mcp in Claude Code) or restarts.');
}

/// Offered once per workspace, on the first live session, which is when the notes become useful.
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

/// Assemble the report, show it, and offer a mail draft. Nothing is sent from this side: the user reads the
/// report and does the sending.
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
        'Report ready. Nothing has been sent. Open a mail draft with it?',
        'Open mail draft', 'Show the file', 'Not now');

    if (answer === 'Open mail draft') {
        await vscode.env.openExternal(vscode.Uri.parse(draft.mailto));
        vscode.window.showInformationMessage('Attach the report file, read it over, then send it.');
    } else if (answer === 'Show the file') {
        await vscode.commands.executeCommand('revealFileInOS', vscode.Uri.file(draft.path));
    }
}

// ---------------------------------------------------------------------------------------------- pairing

/// Command that starts an agent. An explicit setting wins. The default 'claude' is used only if PATH can
/// resolve it; otherwise the newest Claude Code extension's bundled CLI is used.
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
            .map(name => path.join(
                extensions, name, 'resources', 'native-binary', process.platform === 'win32' ? 'claude.exe' : 'claude'))
            .find(candidate => fs.existsSync(candidate));

        if (bundled) {
            return bundled;
        }
    } catch {
        // No extensions folder is an acceptable outcome; the default is returned below.
    }

    return 'claude';
}

/// Handles the canvas button: vscode://phenome.phenome-link/pair?port=NNNN opens or wakes this window and
/// starts an agent session with the handshake already typed. The port is in the URI and is known before the
/// poll finds the port file.
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

    // The handshake is self-contained on purpose: the URI opens the window that was focused last, and that
    // window's workspace, if it has one, need not know anything about Phenome. The server describes the
    // protocol itself at GET /.
    const where = port
        ? `http://127.0.0.1:${port}`
        : process.platform === 'win32'
            ? 'the port in %TEMP%\\phenome-link-*.port (a stale file has a dead pid)'
            : 'the port in $TMPDIR/phenome-link-*.port (a stale file has a dead pid)';

    const agent = agentCommand();
    // A path is quoted for the shell the terminal runs: PowerShell needs the call operator, and zsh or bash
    // need the path in single quotes with any inner quote closed and reopened. The macOS branch has not been
    // run on a Mac.
    const invoke = !/[\\/]/.test(agent)
        ? agent
        : process.platform === 'win32'
            ? `& '${agent.replace(/'/g, "''")}'`
            : `'${agent.replace(/'/g, "'\\''")}'`;

    // The port goes into the session environment so the agent's MCP server binds to the canvas whose button
    // was pressed, not to the first session that answers. With several Rhinos running, one per agent, that is
    // what keeps an agent on its own canvas.
    const terminal = vscode.window.createTerminal({
        name: port ? `Claude × Grasshopper :${port}` : 'Claude × Grasshopper',
        env: port ? { PHENOME_GH_PORT: String(port) } : undefined,
    });

    terminal.sendText(
        `${invoke} "This session pairs with a live Grasshopper canvas${port ? ` on port ${port}` : ''}. If ` +
        `'phenome' MCP tools (canvas, events, say, ...) are available, use them: each asks permission once, ` +
        `and they are already bound to this canvas${port ? '' : ' by discovery'}. Otherwise the link is ` +
        `loopback HTTP at ${where}. GET / describes the whole protocol; start there. Read the canvas, then ` +
        `greet the user with say (author 'claude'). Poll events?since=N while pairing (the response's ` +
        `'latest' is the next cursor). Every entry carries its author; skip the echo of this session's own ` +
        `entries. The user's messages arrive as kind:'message' entries."`);
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
        vscode.commands.registerCommand('phenomeLink.switchCanvas', () => switchCanvas()),
        vscode.commands.registerCommand('phenomeLink.showCanvas', () => showPicture('canvas')),
        vscode.commands.registerCommand('phenomeLink.showViewport', () => showPicture('viewport')),

        vscode.window.registerUriHandler({ handleUri }),

        vscode.workspace.onDidSaveTextDocument(document => {
            pushSavedScript(document).catch(failed => linkLog(`script push failed: ${failed.message}`));
        }));

    refreshTeaching().catch(failed => linkLog(`updating the agent files failed: ${failed.message}`));

    // Grasshopper heartbeat every 2.5 seconds. A poll costs little with a session and less without one.
    paintLinkStatus();
    link.timer = setInterval(() => { pollLink().catch(() => {}); }, 2500);
}

function deactivate() {
    if (link.timer) {
        clearInterval(link.timer);
    }
}

module.exports = { activate, deactivate };
