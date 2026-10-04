#!/usr/bin/env node
// The Grasshopper link as an MCP server: the same loopback HTTP protocol, exposed as named tools instead of
// shell commands. Permissions are the point as much as ergonomics: a raw Invoke-RestMethod is a different
// command every time and is challenged every time, while phenome__say is one name a user allows once.
//
// The server has no dependencies, by design. It speaks newline-delimited JSON-RPC on stdio and finds sessions through the
// same port files the rest of the family uses. The Phenome Link VS Code extension ("Teach Agents") copies it into
// a workspace, but it runs anywhere node runs and needs no extension present at runtime.

const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync, spawn } = require('child_process');

let port = null;
let rhinoPort = null;

/// The port chosen explicitly through the sessions tool, or null. Discovery returns it ahead of the first
/// session that answers, for as long as it keeps answering; see discover().
let chosen = null;

// ------------------------------------------------------------------------------------------- link client

async function ask(pathname, body) {
    if (port === null) {
        await discover();
    }

    if (port === null) {
        throw new Error('No Grasshopper session. Use phenome__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${port}${pathname}`, {
            method: body ? 'POST' : 'GET',

            // Required since 0.32.0. The header matters here and the body does not: a page cannot set this
            // header on a no-cors request, and requiring it keeps websites off this port. The client header also
            // tells the link that the edit came through the tools, and the link then adds no sentence pointing
            // the caller at them.
            headers: body ? { 'Content-Type': 'application/json', 'X-Phenome-Client': 'mcp' } : undefined,
            body: body ? JSON.stringify({ author: 'claude', ...body }) : undefined,
        });

        return await answer.json();
    } catch (failed) {
        port = null;
        throw new Error(`The Grasshopper session stopped answering (${failed.message}). Try again to rediscover.`);
    }
}

/// The same call, against the Rhino half of the link instead of the canvas half.
///
/// There are two servers because they answer about different things, and one can exist without the other: the
/// canvas link starts with Grasshopper and the Rhino link with Rhino. Anything about the process or the
/// document goes here (commands, the document summary, the command line, whether a dialog is open) and works in
/// a Rhino that never opened a canvas.
///
/// With no Rhino link this falls back to the canvas link instead of failing. Those verbs were on the canvas link
/// first and are still there, and the fallback keeps a pairing with an older plugin working.
async function askRhino(pathname, body) {
    if (rhinoPort === null) {
        await discoverRhino();
    }

    if (rhinoPort === null) {
        if (port !== null || (await sessions()).length > 0) {
            // On the canvas link both of these live at /rhino: one endpoint, distinguished by the method.
            const old = pathname === '/command' || pathname === '/doc' ? '/rhino' : pathname;

            return ask(old, body);
        }

        throw new Error('No Rhino session. Use phenome__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${rhinoPort}${pathname}`, {
            method: body ? 'POST' : 'GET',

            // Required since 0.32.0. The header matters here and the body does not: a page cannot set this
            // header on a no-cors request, and requiring it keeps websites off this port. The client header also
            // tells the link that the edit came through the tools, and the link then adds no sentence pointing
            // the caller at them.
            headers: body ? { 'Content-Type': 'application/json', 'X-Phenome-Client': 'mcp' } : undefined,
            body: body ? JSON.stringify({ author: 'claude', ...body }) : undefined,
        });

        // A 404 from a link that is otherwise alive means an older plugin that never had this verb. The canvas
        // link has always served it, and the call goes there instead of reporting an unavailable endpoint. A
        // mixed-version pairing is normal on a machine that updates one half at a time.
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

/// Every live Rhino link on this machine, newest file first: one per running Rhino, with or without a canvas.
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

/// The Rhino link belonging to the current canvas, or the newest one when there is no canvas.
///
/// Both links of one Rhino carry the same pid, which pairs them. Driving one Rhino's canvas against another
/// Rhino's command line is worse than having no command line.
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

/// Every live session on this machine, newest port file first.
///
/// There is one port file per Rhino, and several Rhinos are expected: two agents working at the same time each
/// need a canvas of their own. PHENOME_GH_PORT pins this server to one of them; the extension sets it when
/// starting an agent for a particular canvas. Without it the newest session is used, which is the one a user
/// opened last.
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

    // A session chosen through the sessions tool outranks the first answer. It is re-applied here because this
    // runs again after any failed call, and it is dropped once it stops answering, which keeps a choice from
    // outliving the Rhino it named.
    if (chosen !== null && live.some(one => one.port === chosen)) {
        port = chosen;
        return;
    }

    chosen = null;

    // Cleared when nothing answers. The last port may belong to a Rhino that has since died, and keeping it
    // would make every later call, launch above all, report a session that does not exist.
    port = live.length > 0 ? live[0].port : null;
}

/// Folders passed to Rhino as RHINO_PACKAGE_DIRS by the last launch that named any. restart reuses them and
/// brings back the same plug-in build instead of the installed one.
let packageDirs = [];

/// The environment a new Rhino starts with: this server's own, plus RHINO_PACKAGE_DIRS when folders were
/// given.
///
/// A plug-in under development loads from its build folder, which Rhino only finds through
/// RHINO_PACKAGE_DIRS. Passing the folder through launch keeps what launch does, which an agent starting Rhino
/// from its own shell loses: the quoting of _Grasshopper, the wait for a new port file and the pairing by pid.
function rhinoEnvironment(dirs) {
    if (dirs !== undefined) {
        const missing = dirs.filter(dir => !fs.existsSync(dir));

        if (missing.length > 0) {
            throw new Error(`No folder at ${missing.join(', ')}; packageDirs takes folders that exist.`);
        }

        packageDirs = dirs;
    }

    if (packageDirs.length === 0) {
        return process.env;
    }

    // path.delimiter is a semicolon on Windows and, following the PATH convention, a colon on macOS. The
    // macOS separator has not been verified on a Mac.
    const already = process.env.RHINO_PACKAGE_DIRS ? process.env.RHINO_PACKAGE_DIRS.split(path.delimiter) : [];

    return { ...process.env, RHINO_PACKAGE_DIRS: [...packageDirs, ...already].join(path.delimiter) };
}

/// The Rhino executable and its launch arguments, with or without Grasshopper, for this platform.
///
/// Windows needs the script argument as `/runscript="_Grasshopper"` passed verbatim, because node's default
/// quoting doubles the quotes and Rhino then runs no script at all. Rhino for Mac takes
/// `-runscript=_Grasshopper` with a dash, as an argument of its own, and reads the Windows form as a file name
/// to open. The macOS branch follows McNeel's forum and has not been run on a Mac. PHENOME_RHINO overrides the
/// path on either platform, for a Rhino installed elsewhere.
function rhinoStart(withGrasshopper) {
    const mac = process.platform === 'darwin';
    const exe = process.env.PHENOME_RHINO || (mac
        ? '/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros'
        : 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe');

    if (!fs.existsSync(exe)) {
        throw new Error(`Rhino 8 is not at ${exe}; start it manually, or set PHENOME_RHINO to its path.`);
    }

    const args = mac
        ? ['-nosplash', ...(withGrasshopper ? ['-runscript=_Grasshopper'] : [])]
        : ['/nosplash', ...(withGrasshopper ? ['/runscript="_Grasshopper"'] : [])];

    return { exe, args, verbatim: !mac };
}

/// The Rhino this server started and has not yet seen answer: {pid, withGrasshopper, before, at}.
///
/// Kept so that a launch that ran out of time is not followed by a second Rhino. Rhino sometimes shows a window
/// before its main one (sign-in, licence, crash report, update) and loads no plug-in until that dialog is
/// answered. One measured start took about three minutes that way: launch gave up after 90 seconds and
/// reported a plug-in problem, and Rhino came up on its own afterwards.
let starting = null;

function alive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    } catch {
        return false;
    }
}

/// Title of a process's main window, or null when it cannot be read.
///
/// Windows only, read through PowerShell. The link inside Rhino has not loaded yet at this point, and nothing
/// in Rhino can be asked.
function windowTitle(pid) {
    if (process.platform !== 'win32') {
        return null;
    }

    try {
        return execFileSync(
            'powershell',
            ['-NoProfile', '-Command', `(Get-Process -Id ${pid} -ErrorAction Stop).MainWindowTitle`],
            { encoding: 'utf8', timeout: 8000, windowsHide: true, stdio: ['ignore', 'pipe', 'ignore'] }).trim() || null;
    } catch {
        return null;
    }
}

async function launch(fresh, withGrasshopper = true, dirs = undefined) {
    const env = rhinoEnvironment(dirs);

    // A Rhino this server started that has not answered yet is waited for again instead of started twice. This
    // is checked before anything else, fresh:true included, because that Rhino is already the fresh one.
    if (starting !== null && alive(starting.pid)) {
        return waitForBirth(starting);
    }

    starting = null;

    const list = withGrasshopper ? sessions : rhinoSessions;
    const before = (await list()).map(one => one.port);

    if (!fresh) {
        // Refuse only for a session that is actually answering. A pin (PHENOME_GH_PORT) outlives the Rhino it
        // names. Checking `port` alone would leave an agent whose canvas has died unable to start another one,
        // and that is exactly when it needs one.
        if (withGrasshopper) {
            await discover();

            if (port !== null && before.includes(port)) {
                return `A session already runs on port ${port}.`;
            }

            port = null;
        } else {
            await discoverRhino();

            if (rhinoPort !== null && before.includes(rhinoPort)) {
                return `A Rhino session already answers on port ${rhinoPort}.`;
            }

            rhinoPort = null;
        }
    }

    // The plugin loads with Grasshopper, and without Grasshopper there is no canvas link. rhinoStart
    // documents why the arguments are spelt the way they are.
    const start = rhinoStart(withGrasshopper);

    const child = spawn(start.exe, start.args, {
        detached: true,
        stdio: 'ignore',
        windowsVerbatimArguments: start.verbatim,
        env,
    });

    child.unref();

    starting = { pid: child.pid, withGrasshopper, before, at: Date.now() };

    return waitForBirth(starting);
}

/// Wait up to 90 seconds for the Rhino in `started` to answer, and report what it is doing if it does not.
///
/// The process is matched by the pid in its port file, and failing that by a port that was not there before.
/// Waiting for any session would return the one already running and put two agents on one canvas. 90 seconds
/// per call keeps one tool call from holding the agent for minutes; a later launch keeps waiting for the same
/// process.
async function waitForBirth(started) {
    const list = started.withGrasshopper ? sessions : rhinoSessions;

    for (let waited = 0; waited < 90_000; waited += 3000) {
        await new Promise(rest => setTimeout(rest, 3000));

        const now = await list();
        const born = now.find(one => one.pid === started.pid)
            ?? now.find(one => !started.before.includes(one.port));

        if (born) {
            starting = null;

            if (!started.withGrasshopper) {
                rhinoPort = born.port;

                return `Rhino is up without Grasshopper; the Rhino link answers on port ${rhinoPort} `
                    + `(process ${born.pid}). ${now.length} Rhino link(s) live on this machine. `
                    + 'Canvas tools will not work here; launch again for one.';
            }

            port = born.port;

            // Cleared here; the next Rhino call pairs by pid with the canvas just created. The old value names
            // the Rhino this server spoke to before. After a crash or a closed window that port is dead and the
            // first camera call fails. After fresh:true it is alive but belongs to another Rhino, and the
            // camera would move the wrong window.
            rhinoPort = null;

            // The pin names this agent's canvas, and this is now that canvas. Left pointing at the Rhino that
            // died, the pin would win every later rediscovery and send the agent back to a port nothing
            // answers on.
            if (process.env.PHENOME_GH_PORT) {
                process.env.PHENOME_GH_PORT = String(port);
            }

            return `Rhino is up; the link answers on port ${port} (process ${born.pid}). `
                + `${now.length} session(s) live on this machine.`
                + (packageDirs.length > 0 ? ` RHINO_PACKAGE_DIRS: ${packageDirs.join(path.delimiter)}.` : '');
        }

        if (!alive(started.pid)) {
            starting = null;

            throw new Error(
                `Rhino (process ${started.pid}) ended ${Math.round((Date.now() - started.at) / 1000)} s after it `
                + 'was started, before the link answered. It crashed or was closed.');
        }
    }

    const seconds = Math.round((Date.now() - started.at) / 1000);
    const title = windowTitle(started.pid);
    const again = 'Call launch again to keep waiting: it waits for this same process instead of starting another.';

    // A main window carries Rhino's name or the document name in its title; anything else at this stage is one
    // of the small windows Rhino shows before its main window.
    if (title !== null && /Rhino 8|Untitled|\.3dm/i.test(title)) {
        return `NOT UP YET. Rhino (process ${started.pid}) has its main window open ("${title}"), but the link `
            + `has not answered after ${seconds} s. `
            + (started.withGrasshopper
                ? 'Grasshopper may still be loading, or the plugin did not load. '
                : 'The Rhino plug-in may still be loading, or it did not load. ')
            + `${again} If it stays like this, the user can check Rhino's command line.`;
    }

    return `NOT UP YET. Rhino (process ${started.pid}) is running, but the link has not answered after `
        + `${seconds} s` + (title !== null ? ` and its only window is titled "${title}"` : '') + '. '
        + 'That is usually a dialog Rhino shows before its main window (sign-in, licence, a crash report or '
        + 'an update), and nothing loads until it is answered. Ask the user to look at the screen. '
        + again;
}

/// What a restart would discard, listed by name.
///
/// Both halves are asked, because a session can have unsaved work in either and the process ends both.
/// Failures are ignored on purpose: a half that is not answering has nothing to lose, and a check that failed
/// only because it could not run would push users toward the unsafe path.
async function unsaved() {
    const outstanding = [];

    try {
        const doc = await askRhino('/doc');

        if (doc?.modified) {
            outstanding.push(`the Rhino document (${doc.name ?? 'unsaved'})`);
        }
    } catch {
        // No Rhino half, or no document open in it.
    }

    try {
        if (port !== null || (await sessions()).length > 0) {
            for (const one of (await ask('/documents'))?.documents ?? []) {
                if (one.modified) {
                    outstanding.push(`a Grasshopper document (${one.name})`);
                }
            }
        }
    } catch {
        // An older canvas link without /documents, or none running.
    }

    return outstanding;
}

/// End this agent's Rhino and start a fresh one.
///
/// A .NET plug-in cannot be unloaded: RhinoCommon has LoadPlugIn and no UnloadPlugIn. A rebuilt .rhp or .gha
/// reaches Rhino only through a new process, and restart is the unit of iteration for plug-in development.
/// Doing it by hand takes two tools plus the knowledge that launch will not end the old process.
///
/// The process is ended outright, with no save prompt on the way out, and work that has not been written is
/// lost. That is why the unsaved check is part of the verb and not advice next to it. Losing work takes an
/// explicit discard:true.
async function restart(discard, withGrasshopper, dirs = undefined) {
    const canvas = await sessions();
    const rhinos = await rhinoSessions();

    // Only this agent's process is ended. A user with two Rhinos open loses only the one the agent was working
    // in. A Rhino still starting also belongs to this agent and is the most likely one to need ending.
    const mine = (starting !== null && alive(starting.pid) ? { pid: starting.pid } : null)
        ?? canvas.find(one => one.port === port)
        ?? rhinos.find(one => one.port === rhinoPort)
        ?? canvas[0]
        ?? rhinos[0];

    if (!mine) {
        return `Nothing was running; this is a start, not a restart. ${await launch(false, withGrasshopper, dirs)}`;
    }

    if (!discard) {
        const outstanding = await unsaved();

        if (outstanding.length > 0) {
            throw new Error(
                `Restarting ends the process with no save prompt on the way out. This would lose: `
                + `${outstanding.join('; ')}. Save first ('save' for the canvas, 'saveandclose' per document), `
                + `or pass discard:true to discard it.`);
        }
    }

    try {
        process.kill(mine.pid);
    } catch (failed) {
        throw new Error(`Could not end process ${mine.pid} (${failed.message}).`);
    }

    for (let waited = 0; waited < 30_000; waited += 500) {
        await new Promise(rest => setTimeout(rest, 500));

        try {
            process.kill(mine.pid, 0);
        } catch {
            break;
        }
    }

    // Cleared before the new process starts, otherwise discovery would keep returning the ports of the process
    // that just died.
    port = null;
    rhinoPort = null;
    chosen = null;
    starting = null;

    return `Ended process ${mine.pid}. ${await launch(false, withGrasshopper, dirs)}`;
}

// ------------------------------------------------------------------------------------------------ tools

const object = (properties, required) => ({ type: 'object', properties, ...(required ? { required } : {}) });
const str = description => ({ type: 'string', description });
const flag = description => ({ type: 'boolean', description });
const ids = description => ({ type: 'array', items: { type: 'string' }, description });

const TOOLS = [
    {
        name: 'canvas',
        description: "Read the Grasshopper document. Without 'as' it returns the full state: every object, wires, values, selection, enabled, preview, data mapping, solver. as:'mermaid' returns a flowchart instead, with groups as subgraphs, red components marked and a map of short node ids to real guids. The flowchart is about a fiftieth of the size of the full state and suits getting oriented in an unfamiliar definition. It carries no data; branch and item counts still come from peek.",
        inputSchema: object({ as: { type: 'string', enum: ['mermaid'], description: 'Omit for the full state.' } }),
        run: args => ask(args.as === 'mermaid' ? '/canvas?as=mermaid' : '/canvas'),
    },
    {
        name: 'events',
        description: "Read the journal after entry N. The response's 'latest' is the next cursor. Every entry carries its author; skip the entries by 'claude', which are this agent's own. The user's messages arrive as kind:'message'.",
        inputSchema: object({ since: { type: 'number', description: 'The cursor; 0 for everything kept.' } }, ['since']),
        run: args => ask(`/events?since=${args.since}`),
    },
    {
        name: 'pulse',
        description: "Report whether Rhino is idle, busy or blocked. It is answered without the Rhino UI thread and responds when other tools do not. When another tool times out, this tells the two causes apart: 'busy' names the running command and how long it has run, and means wait; 'blocked' names the open dialog, and means nothing answers until it is answered: by the dialog tool or by the user clicking it.",
        inputSchema: object({}),
        run: () => askRhino('/pulse'),
    },
    {
        name: 'dialog',
        description: "Answer the dialog Rhino is waiting on: 'button' presses one by name, 'key' types into a dialog that draws its own buttons and cannot be clicked, 'close' declines. Give one of the three; if several are present, 'key' is used first, then 'button', then 'close'. With none this refuses and lists the dialog's buttons instead of assuming a decline. No button is treated as a default yes: on a save prompt the affirmative is whichever of Save and Don't Save was intended, and pulse already lists the buttons. 'expect' names the dialog to answer, and the call refuses if another dialog is open by then, because dialogs are replaced while the request is pending. This verb supersedes dismiss.",
        inputSchema: object({
            button: str('Button label to press, as pulse reports it.'),
            key: str("A key to type, for dialogs with no clickable buttons: the underlined letter, or '{ESC}'."),
            close: flag('Decline: close the dialog, as its X button does.'),
            expect: str('Title of the dialog to answer.'),
        }),
        run: args => askRhino('/dialog', args),
    },
    {
        name: 'dismiss',
        description: "SUPERSEDED by dialog, and still works for callers that already send it. It answers the open dialog with a button by name or a key, and with neither it closes the dialog, which declines. Prefer dialog, where declining is explicit.",
        inputSchema: object({
            button: str("The button to press, exactly as pulse lists it. Omit to close the dialog instead."),
            key: str("A key to type instead of clicking. It is needed when pulse says clickable:false, which means the dialog draws its own buttons and has nothing to click. Use the underlined letter of the required answer."),
            expect: str("The dialog title to answer; refuses if another one is open."),
        }),
        run: args => askRhino('/dismiss', args),
    },
    {
        name: 'escape',
        description: "Post Escape to Rhino, cancelling whatever it is waiting for. Use it when pulse says 'busy' and the command it names expects a click. A scripted interactive command waits for a pick that no script supplies, and meanwhile every tool reports 'busy'. dismiss does not apply here: a command waiting on a pick is not a dialog and has no window to click. The key is only queued; check pulse afterwards to confirm it applied.",
        inputSchema: object({
            times: { type: 'number', description: 'How many levels to cancel; one by default, up to five.' },
        }),
        run: args => askRhino('/escape', { times: args.times ?? 1 }),
    },
    {
        name: 'console',
        description: "Read the tail of Rhino's command line: command and script output, such as selection counts, print output and the reason a command behaved unexpectedly. Read it after any action whose result is not in the response. The buffer is drained when the Rhino UI thread yields, and a long command's output arrives when that command ends; pulse gives the current state. mine:true returns the link's own lines instead. They are excluded by default to keep an agent from mistaking its own requests for Rhino's answers; ask for them when the bridge itself is suspected to be at fault.",
        inputSchema: object({
            tail: { type: 'number', description: 'How many lines back, 1 to 500; default 50.' },
            mine: { type: 'boolean', description: "True for the link's own lines instead of Rhino's." },
        }),
        run: args => args.mine
            ? ask(`/console?tail=${args.tail ?? 50}&mine=true`)
            : askRhino(`/console?tail=${args.tail ?? 50}`),
    },
    {
        name: 'say',
        description: 'Write a message into the journal, for the user or another agent.',
        inputSchema: object({ text: str('What to say.'), to: str('Optional addressee.') }, ['text']),
        run: args => ask('/say', args),
    },
    {
        name: 'components',
        description: 'Search the installed component catalogue by name or description. Top matches carry their true inputs and outputs. Use this before add when the exact ribbon name is uncertain.',
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
            nickname: str('Only for parameters. Never rename a component: that makes the canvas unreadable.'),
        }),
        run: args => ask('/add', args),
    },
    {
        name: 'wire',
        description: "Connect outputs to inputs. Pass all wires in a single call via 'wires': one call per wire means one round trip and one canvas recompute each. Ends are {id, param?}, where param is a name or index and is needed when a component has several on that side; disconnect:true removes a wire. A single {from, to} at the root also works.",
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
        description: "Set values on objects. Pass all values at once in 'values'; a single value at the root also works. A slider takes bounds and precision, or a string like '0<50<100' for all three. With 'param', the value replaces a component input's stored constant (no separate parameter and wire are needed); a null value empties that socket, which is the only way back to nothing stored. On a note (a Scribble or a Panel) the value is its wording, and this rewords an existing note without deleting and rebuilding it. Empty or whitespace wording is refused, because a blank note is indistinguishable from one whose text went missing. A Panel sends its whole text as one item regardless of line breaks, because Grasshopper's Multiline Data is on for every new panel: pass an array (['-3','3','3','-3']) to make it a list source, one item per element. A Colour Swatch takes [r,g,b] or [r,g,b,a], or text as Grasshopper reads it: 'r,g,b', 'r,g,b,a' with the alpha last, '#rrggbb', '#aarrggbb', or a colour name. On any other parameter an array stores one item per element, and [x,y,z] is a point: a Point parameter takes [[0,0,0],[10,0,0]] or ['0,0,0','10,0,0']. A value the parameter cannot read refuses the whole entry instead of storing less. 'nickname' renames a parameter standing on its own (a group's inlet or outlet, a slider, a panel) and is refused on a component, which keeps its name. 'width' and 'height' size a Panel. With nickname, width, or height, 'value' may be omitted.",
        inputSchema: object({
            values: {
                type: 'array',
                description: 'Every value to set: [{id, value?, param?, minimum?, maximum?, decimals?, nickname?, width?, height?}]',
                items: { type: 'object' },
            },
            id: str('Object id.'),
            value: { description: "Number, text, flag, [x,y,z] for a point, or an array of those for a list. For sliders, a string '<min><<value><<max>' sets the whole domain. For a Panel, an array of lines makes it send one item per line." },
            param: str("A component input's name or index; the value becomes that input's stored constant."),
            minimum: { type: 'number', description: 'Slider lower bound.' },
            maximum: { type: 'number', description: 'Slider upper bound.' },
            decimals: { type: 'number', description: 'Slider decimal places; 0 makes it an integer slider.' },
            nickname: str('New name for a parameter standing on its own. Refused on a component.'),
            width: { type: 'number', description: 'Panel width in canvas units.' },
            height: { type: 'number', description: 'Panel height in canvas units.' },
        }),
        run: args => ask('/set', args),
    },
    {
        name: 'arrange',
        description: "Lay the whole document out in layers: sources left, few crossings, even spacing. Within a column, inputs to a component or group are ordered by the sockets they feed, with the source of the first input on top. Groups are laid out as whole blocks, and their frames never overlap. Notes are placed by group: a note in a group becomes that group's caption above its other members; a note in no group becomes the document title above everything. Notes are positioned automatically from the group they are assigned to at placement. It is idempotent: on a settled document it answers moved:0 and changes no coordinates. Run it after building, editing or grouping, and never place anything by hand.",
        inputSchema: object({}),
        run: () => ask('/arrange', {}),
    },
    {
        name: 'signature',
        description: "Give a group named floating parameters at its edges and reattach every crossing wire to them; the group then reads as a virtual component. It is safe to run twice: it recognises the ports it created and reuses them. Run it once, after all grouping and renaming is settled. Omit id to do every group.",
        inputSchema: object({ id: str('Group id; omit for all groups.') }),
        run: args => ask('/signature', args),
    },
    {
        name: 'preview',
        description: "Turn a preview off, leaving the viewport to show the product without the intermediate steps that produced it (cutting boxes, construction curves, the 23,000 point markers from an intermediate interpolation). Use it during the build as well as at the end: when an intermediate output clutters the viewport, name that component here and it stops drawing. Takes a group id or a single object id, and 'ids' for a batch of either; a named object that draws nothing is skipped and listed under 'skipped', and only an id that is not on the canvas refuses the batch. With no id it applies to the whole document: only the outlets of red and yellow groups keep drawing, objects in no group are quieted too, and a canvas with no groups goes fully quiet. Use that full sweep after review and before save. on:true restores the preview.",
        inputSchema: object({
            id: str('A group id or a single object id. Omit entirely to sweep the document, leaving only red and yellow groups\' outlets drawing.'),
            ids: { type: 'array', items: { type: 'string' }, description: 'Several ids at once, groups or objects, mixed freely. Use this instead of one call each.' },
            on: { type: 'boolean', description: 'True gives the preview back instead of quieting it.' },
        }),
        run: args => ask('/preview', args),
    },
    {
        name: 'review',
        description: "Lint the definition. Every finding has a severity. 'blocking' means the definition does not run or does the wrong thing: red components, a component run more than 100 times in one branch against data it has already used (branches of unequal count multiplying each other; equal lists paired item by item are fine), an object in two groups, a hidden flatten/graft or simplify, or a group with no signature. All blocking findings must be fixed. 'polish' covers presentation: group sizes, input banks, unnamed groups, ungrouped objects. Fix the blocking ones first, and do not break a working graph to remove polish findings.",
        inputSchema: object({}),
        run: () => ask('/review'),
    },
    {
        name: 'group',
        description: "A named group is a function. Declare its signature first: pass inlets and outlets, which are created as named floating parameters and returned as a name-to-id map ('ports'); place the body and wire it to them. Build in this order: plan, declare every group's signature, fill the bodies, review. Colour by role, four roles only (drawn at quarter opacity): blue [70,110,255] inputs the user may modify, red [255,60,60] components baked to Rhino as the product, yellow [255,220,0] preview-only geometry, grey [150,150,150] a plain function.",
        inputSchema: object({
            name: str('The one thing this group does.'),
            inlets: { type: 'array', description: "This group's inputs: names, or {name, type} where type is number, integer, text, boolean, point, vector, plane, line, curve, surface, brep, mesh, geometry, interval, colour or transform.", items: {} },
            outlets: { type: 'array', description: "This group's outputs, same shape as inlets.", items: {} },
            id: str('An existing group to rename, recolour or add members to. Use this instead of ungrouping and regrouping.'),
            ids: ids('Existing objects to enclose, when grouping after the fact.'),
            colour: { type: 'array', items: { type: 'number' }, description: '[r, g, b], picked by the role convention in the description.' },
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
        description: "Remove objects. If deleting would cut connections to objects that stay, it refuses and names those wires first; read that list before using force, because it indicates the objects are still referenced. force:true deletes anyway. A delete itself is on Grasshopper's undo stack; this does not make a document close reversible.",
        inputSchema: object({
            ids: ids('Object ids.'),
            force: flag('Delete even though live wires would be cut.'),
        }, ['ids']),
        run: args => ask('/delete', args),
    },
    {
        name: 'describe',
        description: "A placed object's parameters: names, nicknames, types, item-or-list access, and the wire and item counts each holds. Use this on an object already on the canvas instead of searching the catalogue for parameter names. On a note (a Scribble or a Panel) it also returns 'annotation' with the text as it reads, its position, the rectangle it covers, and the group it is in; use that to verify wording and placement without a screenshot.",
        inputSchema: object({ id: str('Object id.') }, ['id']),
        run: args => ask(`/describe?id=${args.id}`),
    },
    {
        name: 'wires',
        description: "List every wire in the document, from and to, with object names and parameter names. This is the complete connection picture, which querying one input at a time does not give. Use it after any structural change to see what is connected.",
        inputSchema: object({}),
        run: () => ask('/wires'),
    },
    {
        name: 'undo',
        description: "One step back through Grasshopper's undo stack. Every verb records into it, and a delete or a bad arrange can be reverted. Returns the name of the undone step.",
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
        description: 'List the script components on the canvas, with their generation.',
        inputSchema: object({}),
        run: () => ask('/scripts'),
    },
    {
        name: 'script_read',
        description: "Read one script component's source.",
        inputSchema: object({ id: str('Script component id.') }, ['id']),
        run: args => ask(`/script?id=${args.id}`),
    },
    {
        name: 'script_write',
        description: "Write new source into a script component. It answers with the error and warning messages from the recomputation.",
        inputSchema: object({ id: str('Script component id.'), source: str('The whole new source.') }, ['id', 'source']),
        run: args => ask('/script', args),
    },
    {
        name: 'pillscript',
        description: "Work on a PillScript component, the C# script component whose inputs and outputs come from its RunScript signature and whose sources are a project of files. script_read and script_write do not reach it; this does, by calling PillScript in the same Rhino. 'tool' names what to do and 'arguments' carries that tool's own fields. 'component' in arguments is the component's id or a unique prefix of it, and may be left out when the canvas holds one. Tools: list_components {} lists them with ids, files and parameters, so start there. list_files {component}. read_file {component, file}. write_file {component, file, content} replaces or creates a file and does not compile. delete_file {component, file} and rename_file {component, from, to}; Script.cs and Script.csproj stay. list_references {component}. add_package {component, id, version?} and remove_package {component, id} for NuGet, restored at the next compile. add_reference {component, path} to a .dll and remove_reference {component, name}. compile {component} builds and answers the diagnostics and the new parameters; the inputs and outputs change here, so wire after compiling. solve {component} recomputes and answers what the script printed. open_editor {component} opens the editor window for the user. Needs PillScript 0.5.0 or later; the refusal says when it is missing or older.",
        inputSchema: object({
            tool: {
                type: 'string',
                enum: ['list_components', 'list_files', 'read_file', 'write_file', 'delete_file', 'rename_file',
                    'list_references', 'add_package', 'remove_package', 'add_reference', 'remove_reference',
                    'compile', 'solve', 'open_editor'],
                description: 'What to do.',
            },
            arguments: { type: 'object', description: "The tool's fields, e.g. {component:'7ef9', file:'Script.cs'}." },
        }, ['tool']),
        run: args => ask('/pillscript', args),
    },
    {
        name: 'new_document',
        description: 'Open a fresh Grasshopper document on the canvas.',
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
        name: 'documents',
        description: "Every document Grasshopper holds open, with id, name, path, unsaved-edit flag, object count, and which one the canvas is showing; pass 'use' with an id to switch to a different one. Read this to confirm which document operations apply to: new_document and open both leave the previous document open but unreachable, so a session often holds several documents, each with its own unsaved edits. It has the same shape as sessions, which selects between running Grasshopper instances instead of between documents inside one.",
        inputSchema: object({ use: str('Id of the document to show from now on. Omit to just read the list.') }),
        run: args => (args.use === undefined ? ask('/documents') : ask('/documents', args)),
    },
    {
        name: 'close',
        description: "Close a document and discard its unsaved changes: the one on the canvas, unless 'id' names another. There is no prompt and no undo: use saveandclose to keep the work, and read documents first if it is unclear what is open or which one is showing. Returns what the canvas shows afterwards, and reports discardedUnsavedChanges when changes were in fact discarded. Closing the last document leaves none; the building verbs create one when needed.",
        inputSchema: object({ id: str('Document id; omit for the one the canvas is showing.') }),
        run: args => ask('/close', args),
    },
    {
        name: 'saveandclose',
        description: "Write the document, then close it. Without 'path' it saves in place; a document that has never been saved is refused instead of written to an unspecified location; pass 'path' for that case. Use this when tidying an accumulated session: documents lists what is open, and each relevant document can be saved and closed in turn.",
        inputSchema: object({
            id: str('Document id; omit for the one the canvas is showing.'),
            path: str('Absolute .gh path, needed when the document has never been saved.'),
        }),
        run: args => ask('/saveandclose', args),
    },
    {
        name: 'report',
        description: "Record a problem with a verb: what was expected versus what happened. Refused requests are logged automatically. This is for the remainder: a tool that ran, but not as its description said. It writes to a local file and costs nothing.",
        inputSchema: object({
            expected: str('What was expected to happen.'),
            got: str('What happened instead.'),
            notes: str('Anything else worth knowing.'),
        }, ['expected', 'got']),
        run: args => ask('/report', args),
    },
    {
        name: 'feedback',
        description: "Assemble the report into one readable file (session, composition review and recent friction log) and return its path and a mailto link. Ask the user first: offer it after repeated trouble and let them send it themselves. Nothing is sent here; the mailto opens their mail client prefilled, with the file to attach.",
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
        description: "Start Rhino with Grasshopper and wait for the link to answer. Use it when there is no session. fresh:true starts another Rhino even when one is already running and works with that one; two agents then each get their own canvas instead of editing the same one. grasshopper:false starts Rhino alone, which is faster and enough for document-level work: open, select, run commands, export. For plug-in work use grasshopper:false: building, installing and loading a Rhino plug-in needs no canvas, and starting Grasshopper adds a slower launch and another component that can fail to load. The plugins, rhino_load, rhino_command, rhino_doc, pulse, dismiss, escape and console verbs all respond in a Rhino that never opened Grasshopper. For a plug-in that loads from its build folder, pass that folder in packageDirs. Rhino reads RHINO_PACKAGE_DIRS only at startup, restart keeps the folders, and Rhino never has to be started from the agent's shell. One call waits 90 seconds. An answer beginning NOT UP YET means the Rhino it started is alive but the link has not answered yet, usually because of a dialog Rhino shows before its main window; ask the user to look. Calling launch again keeps waiting for that same process instead of starting another.",
        inputSchema: object({
            fresh: flag('Start another Rhino and use it, even if a session exists.'),
            grasshopper: flag('False starts Rhino without Grasshopper; canvas tools then have nothing to talk to.'),
            packageDirs: ids('Folders Rhino searches for plug-in packages (RHINO_PACKAGE_DIRS), such as a debug build folder. Kept for later restarts; [] clears them.'),
        }),
        run: args => launch(args.fresh === true, args.grasshopper !== false, args.packageDirs),
    },
    {
        name: 'sessions',
        description: "List every live session on this machine and the one these tools are using. There are two lists for the two links: a canvas session per running Grasshopper, and a Rhino session per running Rhino; a Rhino started without Grasshopper appears only in the second, and commands, pulse and the console still work there. Pass 'use' with a port to route every later call to that session. Otherwise, with two Rhinos open, the session is the newest one that answers; bind to the intended canvas with 'use'.",
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

                // Held for the life of this server. The choice has to survive the rediscovery that follows any
                // failed call; without it the next failure would move the agent to the first session in the list.
                chosen = args.use;
                port = args.use;
            }

            // Resolved here instead of reported blank. "Which one am I on" is answered before the first edit, and
            // the answer never names a port that has stopped answering; a dead session once kept presenting
            // itself as the live one.
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
        description: "Capture the active Rhino viewport as an image, at low resolution by default and framed on the geometry for the capture (the camera is restored afterwards). Use it to inspect built geometry; for canvas layout, read canvas positions instead. The capture redraws the view off-screen at its own size. Geometry drawn by a plug-in's own display code can then be missing, stale or cropped even when the screen shows it correctly, as seen with an off-thread volume preview and with script component outputs. If peek reports geometry that the image does not show, trust peek and ask the user to look before assuming a broken component.",
        inputSchema: object({
            width: { type: 'number', description: 'Pixels across; default 640.' },
            zoomExtents: { type: 'boolean', description: "False captures the user's current framing instead." },
        }),
        run: async args => {
            const answer = await askRhino(`/screenshot?width=${args.width ?? 640}&zoomExtents=${args.zoomExtents ?? true}`);

            if (!answer.png) {
                return answer;
            }

            return { __image: answer.png };
        },
    },
    {
        name: 'plugins',
        description: "Report what Rhino and Grasshopper have: the runtime Rhino is hosting, and a record of every Rhino plug-in, loaded or not, with its recorded path, whether the assembly is managed, whether it is load-protected, and its registry key; plus the Grasshopper libraries when a canvas is open. This is the verb for a plug-in that will not load. A record present with loaded:false rules out the registry in one call, and the runtime line addresses the other common cause: Rhino 8 hosts two CLRs and a plug-in must match the one in use. The Rhino half answers it, and it works in a Rhino that never opened Grasshopper. Shipped plug-ins are excluded unless all:true.",
        inputSchema: object({ all: flag('Include the hundred or so plug-ins that ship with Rhino.') }),
        run: async args => {
            const report = await askRhino(`/plugins${args.all === true ? '?all=true' : ''}`);

            // Grasshopper's own libraries come from the canvas half, and only when a canvas exists. Without a
            // canvas the field is left out: a Rhino without Grasshopper has no libraries, and an empty list would
            // state instead that none are loaded.
            if (Array.isArray(report?.plugins)) {
                try {
                    const canvas = await ask('/plugins');

                    if (Array.isArray(canvas?.grasshopper)) {
                        report.grasshopper = canvas.grasshopper;
                    }
                } catch {
                    // No canvas; the Rhino half has already answered.
                }
            }

            return report;
        },
    },
    {
        name: 'rhino_load',
        description: "Load a Rhino plug-in explicitly, by 'id' from plugins or by 'path' to an .rhp. It loads without prompting, and the confirmation a load-protected plug-in raises does not block the call. Installed plug-ins are load-protected, which makes that dialog the normal case. Do not use the global load-protection setting instead: with load-protection asking off, Rhino does not load protected plug-ins at all, which is indistinguishable from a broken build. It also loads again after a failed attempt. Rhino on its own refuses that, which is why a rebuild-and-load loop appears to do nothing the second time. It returns what Rhino's record reports afterwards, not the call's own result.",
        inputSchema: object({
            id: str('Plug-in id, as plugins reports it.'),
            path: str('Absolute path to an .rhp, for one Rhino has no record of yet.'),
        }),
        run: args => askRhino('/load', args),
    },
    {
        name: 'restart',
        description: "End this agent's Rhino and start a fresh one, waiting until the link answers again. This is the iteration unit for plug-in development. A .NET assembly cannot be unloaded from Rhino (there is LoadPlugIn and no UnloadPlugIn), and a rebuilt .rhp or .gha reaches a running Rhino only through a new process. It refuses while either half holds unsaved work, because the process is ended without a save prompt on the way out; pass discard:true to discard it. Only the process this agent is using is ended, and a second Rhino opened by another agent or the user keeps running.",
        inputSchema: object({
            discard: flag('Restart even though unsaved work would be lost.'),
            grasshopper: flag('False brings Rhino back without Grasshopper, which is faster and enough for plug-in work.'),
            packageDirs: ids('Folders for RHINO_PACKAGE_DIRS. When omitted, the folders from the last launch are used again.'),
        }),
        run: args => restart(args.discard === true, args.grasshopper !== false, args.packageDirs),
    },
    {
        name: 'camera',
        description: "Read or set the active Rhino viewport's camera. With no arguments it returns the camera's projection, location, target, up, 35mm-equivalent lens length, and the viewport's pixel size. Pass any of those to change only that. Use this to frame a view: Rhino's Zoom is an interactive command, and scripting it with a magnification waits for a pick that never comes, holding the UI thread and making every other tool report that Rhino is busy.",
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

            return aiming ? askRhino('/camera', args) : askRhino('/camera');
        },
    },
    {
        name: 'canvas_image',
        description: "Capture the Grasshopper canvas as an image, fitted to the whole document (the view is restored afterwards). Use it after arrange to check whether the layout reads; coordinates and lint findings do not show that.",
        inputSchema: object({
            width: { type: 'number', description: 'Pixels across; default 1200.' },
            fit: { type: 'boolean', description: "False captures the user's current framing instead." },
        }),
        run: async args => {
            const answer = await ask(`/canvas-image?width=${args.width ?? 1200}&fit=${args.fit ?? true}`);

            return answer.png ? { __image: answer.png } : answer;
        },
    },
    {
        name: 'place',
        description: "Place a whole group's body in one call: objects with local ids, wired to each other, to the group's inlet and outlet ids, and to objects already on the canvas. Each object: {id?, name|guid, nickname?, pivot?:[x,y], slider?:{value,minimum,maximum,decimals}, text?, value?, inputs?:[{param?, sources?:[{id, output?}], value?}]}. An input takes 'sources' for wires or 'value' for a constant typed into that socket, and 'param' is a name or an index. Pass 'group' and every object placed joins that group. Returns the local-id to canvas-id map. Prefer this over add/wire loops. 'text' is a note's wording and works on both a Scribble and a Panel; empty text is refused and does not become a placeholder. A Panel sends a string as one item regardless of line breaks; give 'text' as an array of lines when the panel is a list source. 'describe' reads the text back with the note's position, which checks placement and wording without a screenshot. Prefer 'guid' over 'name'. A ComponentGuid is what a .gh file stores and it cannot change, while a display name can be renamed by a plugin author and collides between plugins. The guid also avoids the ambiguity refusal. A recipe is all-or-nothing: if any entry cannot be resolved, nothing is placed and the canvas is untouched. The refusal names every bad entry by its local id; fix them all in one pass and resend the whole recipe instead of probing one at a time.",
        inputSchema: object({
            objects: { type: 'array', items: { type: 'object' }, description: 'The recipe, in dataflow order.' },
            group: str("The group this body belongs to; everything placed joins it."),
        }, ['objects']),
        run: args => ask('/place', args),
    },
    {
        name: 'peek',
        description: "Read the full data on one parameter, branch by branch with tree paths. These are the exact values to verify a definition by, beyond the five-value sample in canvas. Given a group's id instead, it returns that group's signature as it stands: every inlet and outlet with its type, branch and item counts, and a few values from each outlet. That is a function's current type in one call, without needing its ports' ids first.",
        inputSchema: object({
            id: str('Object id.'),
            side: { type: 'string', enum: ['input', 'output'] },
            param: str('Parameter name or index; omit when there is only one.'),
        }, ['id']),
        run: args => ask(`/peek?id=${args.id}${args.side ? `&side=${args.side}` : ''}${args.param !== undefined ? `&param=${encodeURIComponent(args.param)}` : ''}`),
    },
    {
        name: 'measure',
        description: "Measure lengths, areas and volumes of the geometry on one parameter (a component's output unless side:'input'), item by item with tree paths, plus totals and a bounding box. Curves give length, and area when closed and planar; breps and meshes give area, and volume when closed. Pass 'against' with a second object (and againstParam/againstSide) to compare every pair across the two sets: the shared area of two closed planar curves, the shared volume of two solids, overlapping pairs, and the nearest distance between curves or points. Pass the same id and parameter twice to compare a set with itself, each pair once, for example to find whether sections overlap. A call compares at most 2500 pairs. It is read-only and computed on the data already present, which fits a set-and-measure loop. Do not add a script component just to measure.",
        inputSchema: object({
            id: str('Object id.'),
            side: { type: 'string', enum: ['input', 'output'] },
            param: str('Parameter name or index; omit when there is only one.'),
            against: str('A second object id to compare with.'),
            againstSide: { type: 'string', enum: ['input', 'output'] },
            againstParam: str("The second object's parameter name or index."),
        }, ['id']),
        run: args => {
            const query = new URLSearchParams({ id: args.id });

            for (const key of ['side', 'param', 'against', 'againstSide', 'againstParam']) {
                if (args[key] !== undefined) {
                    query.set(key, String(args[key]));
                }
            }

            return ask(`/measure?${query}`);
        },
    },
    {
        name: 'save',
        description: "Save the Grasshopper document to its own path, or to 'path' if it was never saved. Call it after finishing edits: an unsaved canvas holds the session's work only in a running process. A one-time autosave also runs before the first edit and does not replace saving.",
        inputSchema: object({ path: str('Absolute .gh path, when the document has none yet.') }),
        run: args => ask('/save', args),
    },
    {
        name: 'zoom',
        description: 'Focus the canvas view on those objects. Use it with select to direct the user to a place on the canvas.',
        inputSchema: object({ ids: ids('Object ids to frame.') }, ['ids']),
        run: args => ask('/zoom', args),
    },
    {
        name: 'rhino_command',
        description: "Run a Rhino command script: layers, blocks, groups, anything the command line accepts. Use the scripting dialect: a leading '-' suppresses dialogs, e.g. \"-_Layer New Walls Enter\".",
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

/// The composition rules, returned from initialize.
///
/// This is the only channel that does not depend on a file being read: a client puts a server's instructions
/// into the model's context before the first tool call. AGENTS.md is a convention some agents follow and
/// others do not (Claude reads CLAUDE.md, others read neither), and the notes travel with the tools for that
/// reason. They are read from the workspace's own AGENTS.md when present, which keeps one source that this
/// file cannot drift from. The summary below is the fallback.
function instructions() {
    try {
        const notes = fs.readFileSync(path.join(process.cwd(), 'AGENTS.md'), 'utf8');
        const start = notes.indexOf('<!-- phenome-link:start -->');
        const end = notes.indexOf('<!-- phenome-link:end -->');

        if (start >= 0 && end > start) {
            return notes.slice(start, end).replace('<!-- phenome-link:start -->', '').trim();
        }
    } catch {
        // No workspace notes; the summary below is used.
    }

    return [
        'These tools drive a live Grasshopper canvas. Follow these rules:',
        '',
        '1. A group is a function and does exactly one thing; name every group for that thing.',
        '2. Declare each group with its signature first (`group` takes inlets and outlets and answers a',
        '   name-to-id map), then fill its body with one `place` call passing that group id.',
        '3. An object belongs to exactly ONE group. Sharing an object between groups makes `signature` refuse.',
        '4. Batch calls: `wire` takes wires:[...], `set` takes values:[...]. Never send one call per wire.',
        '5. Colour by role: blue [70,110,255] user inputs, grey [150,150,150] plain function,',
        '   red [255,60,60] geometry baked to Rhino, yellow [255,220,0] preview only.',
        '6. Sliders get real domains ("1000<2000<3000"); a constant goes in the socket via `set` with param.',
        '7. Never rename a component. Never use the simplify modifier. Flatten and graft as components.',
        '8. Data travels on wires, one value per wire, never packed into text.',
        '9. Two wires into one socket meet only where their paths agree: sources at different depths',
        '   ({0} and {0;0}) never share a branch. `peek` the input, not the output.',
        '10. Never position anything by hand: `arrange` lays groups out as blocks, and places notes by the',
        '    group they belong to. In a group a note is that group\'s caption; in none it is the document title.',
        '11. A scribble is a comment, a group\'s name is the function signature. Do not write both saying the',
        '    same thing; a caption that repeats the group name adds nothing. Most groups need no caption.',
        '12. Finish with `signature` once, `arrange`, then `review`; fix every finding marked blocking and',
        '    treat polish as optional. Then call `preview` with no id, which leaves only the outlets of the red',
        '    and yellow groups drawing and darkens the scaffolding, and then `save`.',
        '',
        'Verify numerically with `peek` (branch and item counts are the specification), measure lengths,',
        'areas and overlaps with `measure`, and look at the layout with `canvas_image`. If a tool behaves',
        'unexpectedly, report it with `report` instead of working around it with a temporary script component.',
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
            // One at a time, in arrival order: a launch must finish creating the session before the call behind
            // it asks that session for anything.
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
                serverInfo: { name: 'phenome', version: '0.34.1' },
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
            // Notifications (initialized, cancelled) are ignored; an unknown request with an id gets an error.
            if (id !== undefined) {
                complain(id, `Method '${method}' is not supported.`);
            }
    }
}
