# Working on the canvas from another place

Rhino stays on the machine it is on. The editor goes to that machine over a VS Code tunnel, and the link
itself never crosses a network.

The setup needs nothing from Phenome. The link binds `127.0.0.1`, the agent runs beside Rhino, and the only
thing that travels is the editor window.

## Steps

**On the machine Rhino is on**, in VS Code, open the Command Palette and run
**Remote Tunnels: Turn on Remote Tunnel Access…**. It asks for a sign-in with a GitHub or Microsoft account
and for a name for the machine. That name is the whole address from then on.

**From anywhere else**, open `https://vscode.dev/tunnel/<that name>` and sign in with the same account. The
browser shows VS Code running against that machine, with its files, its terminal and its extensions.

**Then pair as usual.** The Phenome extension and the MCP server run on the Rhino machine, and everything
below them behaves exactly as it does for someone sitting at that machine.

The tunnel is a feature of VS Code. It costs nothing and works the same with or without this plugin.

## The result

An agent sees the canvas, the Rhino document and the journal on a machine nobody is sitting at, driven from a
browser that needs nothing installed. A phone works, though it is not comfortable.

Extensions run **on the remote machine**. Install Claude Code, Kilo or whichever client drives the tools
**there**, not in the browser, which is only a display.

Install them **from inside the tunnel session**, through the Extensions view or `Install from VSIX…` in its
`…` menu. A tunnel keeps its own extension directory, separate from the one the double-clicked VS Code uses,
and neither sees the other. The Phenome `.vsix` is the extension most likely to run into this, because it
comes as a file and not from the marketplace. Installed at the machine, or over SSH with
`code --install-extension`, it is missing from the tunnel session and nothing says why. The command line can
reach the right directory, but only when told which one:
`code --extensions-dir "%USERPROFILE%\.vscode-server\extensions" --install-extension phenome-link-<version>.vsix`.

## Limitations

**The machine has to be awake, logged in, and left that way.** Rhino needs a desktop session, and a machine
that sleeps takes the tunnel and the canvas down with it. Wake-on-LAN brings the machine back but not the
login. A session that is logged in and merely *disconnected* survives sleep, and that is the state to leave
it in. Switch sleep off, or stay logged in and disconnect.

**The traffic goes through Microsoft's tunnel service.** It does not go to the authors of the link, and it
does not go directly between the browser and the machine. Any hosted remote-desktop or tunnel product makes
the same trade-off.

**It is tied to one account.** The tunnel reaches the account holder's own machine and gives a colleague no
access to it. There is no sharing, no groups, and no audit beyond what the account already carries.

**The pair button on the canvas does not reach the browser.** It opens a `vscode://` link, which wakes the
VS Code installed on the *Rhino* machine and not the browser in use. When working remotely, pair from the
editor side instead: the extension finds the session on its own, and `sessions` lists them if there are
several.

**Starting Rhino is still a separate step.** The `launch` tool does it from the agent once the tunnel session
is open. When Rhino is started by hand from a command line, the form of the argument matters more than it
looks: `docs/protocol.md` measures the three ways of writing it, and only one of them opens a canvas.

## What this page does not cover

This page covers **an agent and Rhino on the same machine**, which is how most people run it. An agent on
one machine driving a Rhino on another is a different problem with a different answer, and nothing above
solves it. Notes on that case are in [link-over-the-network.md](link-over-the-network.md); none of it ships
yet.
