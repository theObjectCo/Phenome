# Working on your canvas from somewhere else

Rhino stays on the machine it is on. You go to the machine rather than bringing the canvas to you, and the
link never crosses a network at all.

That is the whole idea, and it is why this needs nothing from us. The link binds `127.0.0.1`, the agent runs
beside Rhino, and the only thing that travels is your editor window.

## What to do

**On the machine Rhino is on**, in VS Code, open the Command Palette and run
**Remote Tunnels: Turn on Remote Tunnel Access…**. It asks you to sign in with a GitHub or Microsoft account
and to name the machine. That name is the whole address from then on.

**From anywhere else**, open `https://vscode.dev/tunnel/<that name>` and sign in with the same account. You
get VS Code running against that machine: its files, its terminal, and its extensions.

**Then pair as usual.** The Phenome extension and the MCP server are running on the Rhino machine, so
everything below them is exactly as it is when you sit at it.

Nothing here is ours. It is a feature of VS Code, it costs nothing, and it works the same whether you have
this plugin or not.

## What you actually get

An agent that sees the canvas, the Rhino document and the journal, on a machine you are not sitting at, from
a browser that needs nothing installed. A phone will do at a push, though you will not enjoy it.

Extensions run **on the remote machine**, which is the part people trip on: install Claude Code, Kilo or
whatever you drive the tools with **there**, not in the browser. The browser is a screen, not a computer.

Install them **from inside the tunnel session** - the Extensions view, or `Install from VSIX…` in its `…`
menu. A tunnel keeps its own extension directory, separate from the one the VS Code you double-click uses,
and the two do not see each other. This bites hardest with our own `.vsix`, because it comes as a file
rather than from the marketplace: install it by sitting at the machine, or over SSH with
`code --install-extension`, and the tunnel session will not have it and will give no hint why. The command
line can reach the right directory, but only if told which one:
`code --extensions-dir "%USERPROFILE%\.vscode-server\extensions" --install-extension phenome-link-<version>.vsix`.

## What it does not do, and would be unkind not to say

**The machine has to be awake, logged in, and left that way.** Rhino needs a desktop session, so a machine
that sleeps takes the tunnel and the canvas with it. Wake-on-LAN brings the machine back but not the login;
a session that is logged in and merely *disconnected* survives sleep and is the state you want. Switch sleep
off, or leave yourself logged in and disconnected.

**The traffic goes through Microsoft's tunnel service.** Not to us, and not directly to you either. That is
the same trade as any hosted remote-desktop or tunnel product, and it is worth knowing rather than assuming.

**It is tied to your own account.** This is a way to reach your own machine, not a way to give a colleague
access to it. There is no sharing, no groups, and no audit beyond what your account already carries.

**The pair button on the canvas will not reach you.** It opens a `vscode://` link, which wakes the VS Code
installed on the *Rhino* machine rather than the browser you are looking at. Working remotely, pair from the
editor side instead: the extension finds the session on its own, and `sessions` lists them if there are
several.

**Starting Rhino is still a thing somebody has to do.** The `launch` tool will do it from the agent once you
are in, and if you ever start Rhino yourself with a command line, the argument form matters more than it
looks - see `docs/protocol.md`, where the three ways of writing it are measured and only one opens a canvas.

## What this is not

This is the case where **the agent and Rhino are on the same machine**, which is how most people run it. The
other case - an agent on one machine driving a Rhino on another - is a different problem with a different
answer, and it is not solved by any of the above. Notes on that are in
[link-over-the-network.md](link-over-the-network.md); nothing of it ships yet.
