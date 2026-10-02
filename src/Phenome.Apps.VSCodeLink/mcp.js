#!/usr/bin/env node
// The Grasshopper link as an MCP server: the same loopback HTTP protocol, wrapped so that agents see named
// tools instead of shell commands. The point is permissions as much as ergonomics - a raw Invoke-RestMethod
// is a different command every time and gets challenged every time, while phenome__say is one name a
// user allows once.
//
// Deliberately dependency-free: newline-delimited JSON-RPC on stdio, discovery by the same port files the
// rest of the family uses. Copied into a workspace by the Phenome Link VS Code extension ("Teach Agents"),
// but runs anywhere node runs - the extension is a courier, not a dependency.

const fs = require('fs');
const os = require('os');
const path = require('path');
const { execFileSync, spawn } = require('child_process');

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
        throw new Error('No Grasshopper session. Use phenome__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${port}${pathname}`, {
            method: body ? 'POST' : 'GET',

            // Required since 0.32.0, and the reason is the header rather than the body: a page cannot set
            // this on a no-cors request, and demanding it is what keeps a website off this port. The client
            // header tells the link that this edit came through the tools, and the link does not point at them.
            headers: body ? { 'Content-Type': 'application/json', 'X-Phenome-Client': 'mcp' } : undefined,
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

        throw new Error('No Rhino session. Use phenome__launch to start one.');
    }

    try {
        const answer = await fetch(`http://127.0.0.1:${rhinoPort}${pathname}`, {
            method: body ? 'POST' : 'GET',

            // Required since 0.32.0, and the reason is the header rather than the body: a page cannot set
            // this on a no-cors request, and demanding it is what keeps a website off this port. The client
            // header tells the link that this edit came through the tools, and the link does not point at them.
            headers: body ? { 'Content-Type': 'application/json', 'X-Phenome-Client': 'mcp' } : undefined,
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

/// Folders handed to Rhino as RHINO_PACKAGE_DIRS by the last launch that named any, so that restart brings
/// back the same plug-in build rather than the installed one.
let packageDirs = [];

/// The environment a new Rhino is started with: this server's own, plus RHINO_PACKAGE_DIRS when folders
/// were given.
///
/// A plug-in under development loads from its build folder, and Rhino finds a folder like that only
/// through RHINO_PACKAGE_DIRS. launch had no way to pass it. An agent working on a plug-in started Rhino
/// from its own shell and lost everything launch does: the right quoting of _Grasshopper, the wait for a
/// new port file, and the pairing by pid.
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

    // path.delimiter: a semicolon on Windows and, by the same convention as PATH, a colon on macOS. The
    // colon is an assumption nobody has checked on a Mac yet.
    const already = process.env.RHINO_PACKAGE_DIRS ? process.env.RHINO_PACKAGE_DIRS.split(path.delimiter) : [];

    return { ...process.env, RHINO_PACKAGE_DIRS: [...packageDirs, ...already].join(path.delimiter) };
}

/// The Rhino executable and the arguments that open it, with or without Grasshopper, on this system.
///
/// Windows wants the script argument as `/runscript="_Grasshopper"`, passed verbatim, because node's default
/// quoting doubles the quotes and Rhino then runs no script at all. Rhino for Mac takes
/// `-runscript=_Grasshopper` with a dash, as an argument of its own, and reads the Windows spelling as the name
/// of a file to open. The macOS half follows McNeel's forum and has not been run on a Mac. PHENOME_RHINO
/// overrides the path on either system, for a Rhino installed somewhere else.
function rhinoStart(withGrasshopper) {
    const mac = process.platform === 'darwin';
    const exe = process.env.PHENOME_RHINO || (mac
        ? '/Applications/Rhino 8.app/Contents/MacOS/Rhinoceros'
        : 'C:\\Program Files\\Rhino 8\\System\\Rhino.exe');

    if (!fs.existsSync(exe)) {
        throw new Error(`Rhino 8 is not at ${exe}; start it by hand, or set PHENOME_RHINO to where it is.`);
    }

    const args = mac
        ? ['-nosplash', ...(withGrasshopper ? ['-runscript=_Grasshopper'] : [])]
        : ['/nosplash', ...(withGrasshopper ? ['/runscript="_Grasshopper"'] : [])];

    return { exe, args, verbatim: !mac };
}

/// The Rhino this server started and has not yet seen answer: {pid, withGrasshopper, before, at}.
///
/// Kept so that a launch which ran out of time is not followed by a second Rhino. Rhino sometimes shows a
/// window before its main one - a sign-in, a licence question, a crash report, an update - and loads no
/// plug-in until somebody answers it. One start took about three minutes like that, launch gave up after 90
/// seconds and blamed the plug-in, and the Rhino came up on its own afterwards.
let starting = null;

function alive(pid) {
    try {
        process.kill(pid, 0);
        return true;
    } catch {
        return false;
    }
}

/// The title of a process's main window, or null where it cannot be read.
///
/// Windows only, through PowerShell. The link inside Rhino is the part that has not loaded yet, and nothing
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

    // A Rhino this server started that has not answered yet is waited for again, not doubled. Checked before
    // anything else, fresh:true included: that Rhino already is the fresh one.
    if (starting !== null && alive(starting.pid)) {
        return waitForBirth(starting);
    }

    starting = null;

    const list = withGrasshopper ? sessions : rhinoSessions;
    const before = (await list()).map(one => one.port);

    if (!fresh) {
        // Refuse only for a session that is actually answering. A pin (PHENOME_GH_PORT) survives the
        // Rhino it named, so checking `port` alone would leave an agent whose canvas has died unable to
        // start another one - the one moment it most needs to.
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

    // The plugin loads with Grasshopper, and without Grasshopper there is no canvas link. rhinoStart says why
    // the arguments are spelt the way they are.
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

/// Waits up to 90 seconds for the Rhino in `started` to answer, and says what it is doing if it does not.
///
/// The process is matched by the pid in its port file, and failing that by a port that was not there before.
/// Waiting for *a* session would hand back the one already running and quietly put two agents on one canvas.
/// 90 seconds per call, so that one tool call does not hold the agent for minutes; a later launch carries on
/// waiting for the same process.
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
                    + 'Canvas tools will not work here - launch again for one.';
            }

            port = born.port;

            // Forgotten here, and the next Rhino call pairs by pid with the canvas just born. The old value
            // names the Rhino this server spoke to before. After a crash or a closed window that port is dead
            // and the first camera call fails. After fresh:true it is alive and belongs to another Rhino, and
            // the camera would turn in the wrong window.
            rhinoPort = null;

            // A pin names this agent's canvas, and this is now that canvas. Left pointing at the Rhino
            // that died, the pin would win every later rediscovery and send the agent back to a port
            // nothing answers on - a session that repairs itself once and then breaks for good.
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
                + 'was started, before the link answered. It crashed or somebody closed it.');
        }
    }

    const seconds = Math.round((Date.now() - started.at) / 1000);
    const title = windowTitle(started.pid);
    const again = 'Call launch again to keep waiting: it waits for this same process instead of starting another.';

    // A main window carries Rhino's name or the document in its title; anything else at this stage is the
    // small window Rhino shows before its main one.
    if (title !== null && /Rhino 8|Untitled|\.3dm/i.test(title)) {
        return `NOT UP YET. Rhino (process ${started.pid}) has its main window open ("${title}"), but the link `
            + `has not answered after ${seconds} s. `
            + (started.withGrasshopper
                ? 'Grasshopper may still be loading, or the plugin did not load. '
                : 'The Rhino plug-in may still be loading, or it did not load. ')
            + `${again} If it stays like this, the human can check Rhino's command line.`;
    }

    return `NOT UP YET. Rhino (process ${started.pid}) is running, but the link has not answered after `
        + `${seconds} s` + (title !== null ? ` and its only window is titled "${title}"` : '') + '. '
        + 'That is usually a dialog Rhino shows before its main window - sign-in, licence, a crash report or '
        + 'an update - and nothing loads until somebody answers it. Ask the human to look at the screen. '
        + again;
}

/// Everything a restart would throw away, named rather than counted.
///
/// Both halves are asked, because a session can have unsaved work in either and the process takes both
/// with it. Failures are swallowed on purpose: a half that is not answering has nothing to lose, and a
/// check that refuses because it could not check would make the safe path the annoying one.
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

/// Ends this agent's Rhino and starts a fresh one.
///
/// The verb exists because a .NET plug-in cannot be unloaded: RhinoCommon has LoadPlugIn and no
/// UnloadPlugIn, so a rebuilt .rhp or .gha reaches Rhino only through a new process. That makes the
/// restart the unit of iteration for anybody developing a plug-in, and doing it by hand is two tools plus
/// knowing that launch will not end the old one.
///
/// The process is ended outright, which is what makes the unsaved check part of the verb rather than
/// advice next to it: there is no save prompt to answer on the way out, so work that has not been written
/// is simply gone. Named destruction again - 'discard' is how you say you meant it.
async function restart(discard, withGrasshopper, dirs = undefined) {
    const canvas = await sessions();
    const rhinos = await rhinoSessions();

    // This agent's process, not every Rhino on the machine: a human with two open should lose only the one
    // the agent was working in.
    // A Rhino still on its way up is this agent's too, and the likeliest one to need ending.
    const mine = (starting !== null && alive(starting.pid) ? { pid: starting.pid } : null)
        ?? canvas.find(one => one.port === port)
        ?? rhinos.find(one => one.port === rhinoPort)
        ?? canvas[0]
        ?? rhinos[0];

    if (!mine) {
        return `Nothing was running, so this is a start rather than a restart. ${await launch(false, withGrasshopper, dirs)}`;
    }

    if (!discard) {
        const outstanding = await unsaved();

        if (outstanding.length > 0) {
            throw new Error(
                `Restarting ends the process and there is no save prompt on the way out, so this would lose: `
                + `${outstanding.join('; ')}. Save first - 'save' for the canvas, 'saveandclose' per document - `
                + `or pass discard:true if losing it is what you meant.`);
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

    // Forgotten before the new one is started, or discovery would keep handing back the ports of the
    // process that just died.
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
        name: 'dialog',
        description: "Answer the dialog Rhino is waiting on: 'button' presses one by name, 'key' types into a dialog that draws its own buttons and cannot be clicked, 'close' declines. Send one of the three - with no answer given this refuses and lists what the dialog offers, rather than assuming you meant to decline. Nothing here guesses which button means yes, and you should not want it to: on a save prompt the affirmative is whichever of Save and Don't Save you meant, and pulse already lists the buttons. 'expect' names the dialog you meant to answer and refuses if another one is up by then, because dialogs are replaced while you are deciding. Supersedes dismiss.",
        inputSchema: object({
            button: str('Button label to press, as pulse reports it.'),
            key: str("A key to type, for dialogs with no clickable buttons - the underlined letter, or '{ESC}'."),
            close: flag('Decline: close the dialog, which is what its X does.'),
            expect: str('Title of the dialog you meant to answer.'),
        }),
        run: args => askRhino('/dialog', args),
    },
    {
        name: 'dismiss',
        description: "SUPERSEDED by dialog, and kept working for callers that already send it. Answers the open dialog: a button by name, a key, or - with neither - closes it, which declines. Prefer dialog, where declining is something you say rather than something you leave out.",
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
        description: "Values into objects. PASS THEM ALL AT ONCE in 'values'; a single one at the root works for a one-off. A slider takes bounds and precision, or a string like '0<50<100' for all three. With 'param', the value replaces a component input's stored constant - no standalone parameter and wire for the number two - and a null value empties that socket, which is the only way back to nothing stored. On a note - a Scribble or a Panel - the value is its wording, which makes this the way to reword a note you already placed rather than deleting and rebuilding it; empty or whitespace is refused, because a blank note looks exactly like one whose text went missing. A PANEL SENDS ITS WHOLE TEXT AS ONE ITEM, however many lines it has, because Grasshopper's Multiline Data is on for every new panel: pass an array (['-3','3','3','-3']) to make it a list source, one item per element. A Colour Swatch takes [r,g,b] or [r,g,b,a], or text as Grasshopper reads it: 'r,g,b', 'r,g,b,a' with the alpha LAST, '#rrggbb', '#aarrggbb' or a colour name. On any other parameter an array stores one item per element, and [x,y,z] is a point, so a Point parameter takes [[0,0,0],[10,0,0]] or ['0,0,0','10,0,0']; a value the parameter cannot read refuses the whole entry instead of storing less. 'nickname' renames a parameter standing on its own - a group's inlet or outlet, a slider, a panel - and is refused on a component, which keeps its name. 'width' and 'height' size a Panel, so a one-line panel need not keep the default box. With nickname, width or height, 'value' can be left out.",
        inputSchema: object({
            values: {
                type: 'array',
                description: 'Every value to set: [{id, value?, param?, minimum?, maximum?, decimals?, nickname?, width?, height?}]',
                items: { type: 'object' },
            },
            id: str('Object id.'),
            value: { description: "Number, text, flag, [x,y,z] for a point, or an array of those for a list. For sliders, a string '<min><<value><<max>' sets the whole domain. For a Panel, an array of lines makes it send one item per line." },
            param: str("A component input's name or index - the value becomes that input's stored constant."),
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
        description: "Lay the whole document out in layers, mermaid-style: sources left, few crossings, even spacing. Within a column, whatever feeds a component or group stands in the order of the sockets it feeds: the source of the first input on top. Groups are laid out as whole blocks, so their frames never overlap. Notes are placed too, by their group: a note in a group becomes that group's caption and goes above its other members, a note in no group becomes the document's title and goes above everything - so you never position one yourself, you just say which group it is about when you place it. Idempotent: running it on a settled document answers moved:0 and changes no coordinates, so call it as often as you like. Run it after building or editing, and after grouping - never place anything by hand.",
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
        description: "Turns a preview off, so the viewport shows the product instead of every step that made it - the cutting boxes, the construction curves, the 23,000 point markers from an intermediate interpolation. USE THIS DURING THE BUILD, not only at the end: the moment an intermediate output floods the viewport, name that component here and it stops drawing. Takes a group id OR a single object id, and 'ids' for a batch of either; an object named here that draws nothing is skipped and listed under 'skipped', and only an id that is not on the canvas refuses the batch. With no id at all it sweeps the whole document - only the outlets of the RED and YELLOW groups keep drawing, and objects in no group are quieted too, so on a canvas with no groups everything goes quiet - which is the finishing move, run after review and before save. on:true gives the preview back when you want to look inside again.",
        inputSchema: object({
            id: str('A group id or a single object id. Omit entirely to sweep the document, leaving only red and yellow groups\' outlets drawing.'),
            ids: { type: 'array', items: { type: 'string' }, description: 'Several ids at once, groups or objects, mixed freely. Use this instead of one call each.' },
            on: { type: 'boolean', description: 'True gives the preview back instead of quieting it.' },
        }),
        run: args => ask('/preview', args),
    },
    {
        name: 'review',
        description: "Lints the definition. Every finding carries a severity: 'blocking' means the definition does not run or does the wrong thing - red components, a component run more than 100 times in one branch against data it has already used (branches of unequal count multiplying each other; equal lists paired item by item are fine), an object in two groups, a hidden flatten/graft or simplify, a group with no signature - and those must all be fixed. 'polish' means manners: group sizes, input banks, unnamed groups, ungrouped objects. Fix the blocking ones first and never abandon a working graph to chase polish.",
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
        name: 'documents',
        description: "Every document Grasshopper holds open, with its id, name, path, whether it has unsaved edits, how many objects it holds, and which one the canvas is showing - then pass 'use' with an id to work on a different one. Read this when you are not certain which document your verbs are landing in: new_document and open both leave the previous document OPEN and unreachable, so a session usually holds more of them than anybody intended, each keeping its own unsaved edits. Same shape as sessions one level up, which chooses between running Grasshoppers rather than between the documents inside one.",
        inputSchema: object({ use: str('Id of the document to show from now on. Omit to just read the list.') }),
        run: args => (args.use === undefined ? ask('/documents') : ask('/documents', args)),
    },
    {
        name: 'close',
        description: "Close a document AND DISCARD whatever is unsaved in it - the one on the canvas unless you name another with 'id'. There is no prompt and no undo: use saveandclose if the work is worth keeping, and documents first if you are unsure what is open or which one is showing. It answers what the canvas shows afterwards, and says discardedUnsavedChanges when something was in fact thrown away. Closing the last one leaves no document, which is fine - the building verbs make one when they need it.",
        inputSchema: object({ id: str('Document id; omit for the one the canvas is showing.') }),
        run: args => ask('/close', args),
    },
    {
        name: 'saveandclose',
        description: "Write the document, then close it. Without 'path' it saves where it already lives; a document that has never been saved is refused rather than given a location nobody chose, so pass 'path' for that case. This is the verb to reach for when tidying up an accumulated session - documents lists what is open, and each one that matters can be saved and closed in turn.",
        inputSchema: object({
            id: str('Document id; omit for the one the canvas is showing.'),
            path: str('Absolute .gh path, needed when the document has never been saved.'),
        }),
        run: args => ask('/saveandclose', args),
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
        description: "Start Rhino with Grasshopper and wait for the link to answer. Use when there is no session. With fresh:true it starts another Rhino even though one is already running and works with that one - which is how two agents each get a canvas of their own instead of editing the same one. With grasshopper:false it starts Rhino alone - faster, and enough for anything that is about the document rather than a definition: open, select, run commands, export. USE grasshopper:false FOR PLUG-IN WORK: building, installing and loading a Rhino plug-in needs no canvas, and starting Grasshopper for it costs a slower launch and one more thing that can fail to load. The plugins, rhino_load, rhino_command, rhino_doc, pulse, dismiss, escape and console verbs all answer in a Rhino that never opened Grasshopper. For a plug-in that loads from its build folder, pass that folder in packageDirs: Rhino reads RHINO_PACKAGE_DIRS only when it starts, and restart keeps the folders, so the agent never has to start Rhino from its own shell. One call waits 90 seconds, and an answer beginning NOT UP YET means the Rhino it started is alive and the link is not answering yet - most often a dialog Rhino shows before its main window, so ask the human to look - and calling launch again keeps waiting for that same process rather than starting another.",
        inputSchema: object({
            fresh: flag('Start another Rhino and use it, even if a session exists.'),
            grasshopper: flag('False starts Rhino without Grasshopper; canvas tools then have nothing to talk to.'),
            packageDirs: ids('Folders Rhino searches for plug-in packages (RHINO_PACKAGE_DIRS), such as a debug build folder. Kept for later restarts; [] clears them.'),
        }),
        run: args => launch(args.fresh === true, args.grasshopper !== false, args.packageDirs),
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
        description: "The active Rhino viewport as an image - low resolution by default, on purpose, and framed on the geometry for the capture (the camera goes back where the human left it). Use to see what got built; for canvas layout, read canvas positions instead. The capture redraws the view off screen at its own size, so geometry a plug-in draws with its own display code can be missing, stale or cropped while the screen shows it correctly - seen with an off-thread volume preview and with a script component's outputs. When peek reports the geometry and the picture does not show it, trust peek and ask the human to look before calling the component broken.",
        inputSchema: object({
            width: { type: 'number', description: 'Pixels across; default 640.' },
            zoomExtents: { type: 'boolean', description: "False captures the human's current framing instead." },
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
        description: "What Rhino and Grasshopper have: the runtime Rhino is hosting, every Rhino plug-in Rhino holds a record of - loaded or NOT loaded, with the path Rhino believes, whether it thinks the assembly is managed, whether it is load protected, and the registry key - and the Grasshopper libraries when a canvas is open. This is the verb for 'my plug-in will not load': a record that is present with loaded:false rules out the registry in one call, and the runtime line settles the other usual cause, because Rhino 8 hosts two CLRs and a plug-in must match the one that is running. Answered by the Rhino half, so it works in a Rhino that never opened Grasshopper. Shipped plug-ins are left out unless all:true.",
        inputSchema: object({ all: flag('Include the hundred or so plug-ins that ship with Rhino.') }),
        run: async args => {
            const report = await askRhino(`/plugins${args.all === true ? '?all=true' : ''}`);

            // Grasshopper's own libraries are the canvas half's to answer, and only when there is a canvas.
            // Absent rather than empty when there is not: a Rhino without Grasshopper has no libraries,
            // which is a different fact from having none loaded.
            if (Array.isArray(report?.plugins)) {
                try {
                    const canvas = await ask('/plugins');

                    if (Array.isArray(canvas?.grasshopper)) {
                        report.grasshopper = canvas.grasshopper;
                    }
                } catch {
                    // No canvas; the answer that matters is already in hand.
                }
            }

            return report;
        },
    },
    {
        name: 'rhino_load',
        description: "Load a Rhino plug-in on purpose, by 'id' from plugins or by 'path' to an .rhp. Quietly, so the confirmation a load-protected plug-in raises does not stop it - and every plug-in somebody installed is load protected, so that dialog is the normal case. Do NOT reach for the global setting instead: with load-protection asking turned off, Rhino silently does not load a protected plug-in at all, which is the same silence read as a broken build. It also loads again after a failed attempt, which Rhino otherwise refuses - the reason a rebuild-and-load loop appears to do nothing the second time. Answers with what Rhino's record says afterwards, not with the call's own word for 'no'.",
        inputSchema: object({
            id: str('Plug-in id, as plugins reports it.'),
            path: str('Absolute path to an .rhp, for one Rhino has no record of yet.'),
        }),
        run: args => askRhino('/load', args),
    },
    {
        name: 'restart',
        description: "End this agent's Rhino and start a fresh one, waiting until the link answers again. This is the unit of iteration when developing a plug-in: a .NET assembly cannot be unloaded from Rhino - there is LoadPlugIn and no UnloadPlugIn - so a rebuilt .rhp or .gha reaches a running Rhino only through a new process. It refuses while either half holds unsaved work, because the process is ended outright and there is no save prompt on the way out; pass discard:true if losing it is what you meant. Only the process this agent is working in is ended, so a second Rhino somebody else is using survives.",
        inputSchema: object({
            discard: flag('Restart even though unsaved work would be lost.'),
            grasshopper: flag('False brings Rhino back without Grasshopper - faster, and enough for plug-in work.'),
            packageDirs: ids('Folders for RHINO_PACKAGE_DIRS. Omitted, the folders from the last launch are used again.'),
        }),
        run: args => restart(args.discard === true, args.grasshopper !== false, args.packageDirs),
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

            return aiming ? askRhino('/camera', args) : askRhino('/camera');
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
        description: "A whole group's body in one call: objects with local ids, wired to each other, to the group's inlet and outlet ids, and to anything already on the canvas. Each object: {id?, name|guid, nickname?, pivot?:[x,y], slider?:{value,minimum,maximum,decimals}, text?, value?, inputs?:[{param?, sources?:[{id, output?}], value?}]} - an input takes 'sources' for wires OR 'value' for a constant typed straight into that socket, and 'param' is a name or an index. Pass 'group' and everything placed joins that group. Answers the local-id to canvas-id map. Always prefer this over add/wire loops. 'text' is the wording of a note and works on both a Scribble and a Panel; it is refused when empty rather than becoming a placeholder. A Panel sends a string as ONE item whatever its line breaks, so give 'text' as an array of lines when the panel is a list source, and 'describe' reads it back with the note's position so you can check what you wrote without asking anybody to look at the screen. PREFER 'guid' OVER 'name': a ComponentGuid is what a .gh file stores, so it cannot change, while a display name can be renamed by a plugin author and collides between plugins - the guid also skips the ambiguity refusal entirely. A recipe is all-or-nothing: if any entry cannot be resolved NOTHING is placed and the canvas is untouched, and the refusal names EVERY bad entry by your own local id, so fix them all in one pass and send the whole recipe again rather than probing one at a time.",
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
        name: 'measure',
        description: "Lengths, areas and volumes of the geometry on one parameter - a component's OUTPUT unless side:'input' - item by item with tree paths, plus totals and a bounding box. Curves give length, and area when closed and planar; breps and meshes give area, and volume when closed. Pass 'against' with a second object (and againstParam/againstSide) to compare every pair from the two sets: the area two closed planar curves share, the volume two solids share, the pairs that overlap, and the nearest distance between curves or points. Give the same id and parameter twice to compare a set with itself, each pair once - 'do any of these sections overlap'. A call compares at most 2500 pairs. Read-only and computed on the data already there, so it belongs in a loop of set and measure; never stand up a script component to measure.",
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
        'Verify numerically with `peek` (branch and item counts are the specification), measure lengths,',
        'areas and overlaps with `measure`, and look at your layout with `canvas_image`. When a tool fights',
        'you, say so with `report` rather than working round it with a throwaway script component.',
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
                serverInfo: { name: 'phenome', version: '0.34.0' },
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
