# The gateway, read adversarially

An audit of the architecture in `link-over-the-network.md`, written before any of it exists, at the point
where errors are cheapest to find.

## What is protected

**The link is remote code execution by design.** `script_write` compiles and runs C# inside Rhino,
`rhino_command` types anything at the command line, and `/load` loads a plug-in. Reaching a link lets the
caller run code as the user whose Rhino it is.

The value at stake is therefore the machine and the account, not the definition on screen. Every finding
below is weighed against that, and "it is only a Grasshopper plugin" is not used as an argument anywhere in
this document.

Secondary assets, in order: the Windows account password that the network listener is handed; the private
key the gateway binds; the definitions themselves, which are intellectual property; and the session
inventory, which tells an attacker what is worth attacking.

## The boundaries, and what each one is worth

| boundary | trusted because | if it fails |
|---|---|---|
| internet to Cloudflare | the owner configured a policy | anything on the internet reaches the tunnel |
| Cloudflare to host | the tunnel was dialled outbound by the host | Cloudflare sees plaintext, which is accepted |
| cloudflared to gateway | both are on the host | **any local process can pretend to be cloudflared** |
| network client to gateway | TLS, pinned key, a Windows account | whoever holds the account holds the machine |
| gateway to a Rhino link | loopback | unauthenticated, and always has been |
| Rhino to the session folder | a filesystem ACL | a spoofed entry redirects the gateway |
| installer to machine | it was run with administrator rights | the whole machine is lost |

---

## Critical

### 1. The loopback listener must fail closed, and as written it does not

The design says the loopback listener takes "optionally a signed assertion from the edge". Optional implies
a default without one, and with that default an operator can install the gateway, point a tunnel at it, skip
the verifier and **publish remote code execution to the internet**. Nothing visibly breaks, and every caller
is served.

The same ordering hazard makes a raw TCP tunnel dangerous. An HTTP application without its login screen
looks wrong at once, while a proxy that answers correctly gives no sign that no one is being asked to
authenticate.

**Fix.** The listener does not start unless a verifier is configured. A request arriving from loopback is
not taken as proof that it came from cloudflared. Running without a verifier takes a flag the operator sets
by hand, named for what it does, and the service writes that it is running without one to its log at every
start.

### 2. `/s/{port}{verb}` is a request-forgery primitive

The caller chooses the port. Unrestricted, an authenticated user makes a SYSTEM process connect to **any**
loopback port on the host and relay their body to it. On the machine in question that includes Rhino
Compute on 5000, anything else bound to loopback, and any port a future service later takes. Loopback-only
services are often written on the assumption that only a local process can reach them, and this primitive
breaks exactly that assumption.

**Fix.** The port works as a selector. The gateway resolves it against the sessions it currently knows and
refuses anything else. A port missing from the inventory gets a `404`, and the gateway never attempts a
connection to it.

### 3. Authentication without authorisation: any account reaches any canvas

The design proves who is calling and says nothing about which sessions that identity may touch. An
authenticated user therefore drives every Rhino on the machine, including other people's.

The risk is higher on a machine several people use, and those are the machines that get a gateway
installed.

**The obvious fix has a trap.** The session file is written by the plugin running as the user, which makes
its `user` and `session` fields **self-declared**. A local attacker writes a file claiming any identity they
choose and points it at a port they control.

**Fix.** Two parts are needed. Authorise by comparing the caller's identity with the session's owner.
Establish that owner from something the writer does not control: the **file's owner on disk**, or the owner
of the process behind the pid. The fields inside the file are for display only and never feed a decision.

---

## High

### 4. One folder for everyone leaks everyone's ports

Per-user temp directories were an incidental boundary, and moving to a machine-wide folder removes it. Any
local user can now read the inventory of every other user's live links and connect to them directly on
loopback, without passing the gateway or any other check.

The change introduces this regression and it should be recorded as one. The links were already reachable,
since a loopback port scan finds them anyway. What the shared folder changes is the effort: a directory
listing replaces the scan.

**Fix.** The folder grants create-file, and read only to the owner. The gateway runs with system rights and
reads everything regardless. A local client can read its own entries, which are the only ones it uses.

### 5. A world-writable folder in ProgramData is a known shape of vulnerability

A folder in which every user can create files is open to three known attack classes: squatting a name before
the legitimate writer gets there, replacing an entry, and junctions or symlinks pointing the reader somewhere
else entirely.

**Fix.** Grant create-file without delete or modify of other people's entries, deny traversal tricks by
refusing reparse points when reading, and treat a malformed or unexpected entry as absent without trying to
interpret it.

### 6. Account lockout gives the network a remote off switch

Every failed `LogonUser` counts against the account's lockout policy. Reaching the network listener is enough
to lock a person out of their own Windows account by trying a few wrong passwords, without ever getting in.
This denial of service needs no skill and leaves the victim unable to log on at the console either.

**Fix.** Rate-limit by source and refuse early. Attempts then reach `LogonUser` too slowly to matter for the
lockout policy. Consider validating against a dedicated local account instead of the person's daily one. A
lockout then hits the dedicated account and leaves the one they need to use the computer untouched.

### 7. Any account on the machine is an account for this

"A Windows account" includes service accounts, a re-enabled Guest, and every low-privilege account created
years ago. Authentication identifies who is calling; it does not establish that the caller was ever meant to
drive Rhino.

**Fix.** The condition is membership of a local group created at install, and an account outside that group
is refused. Adding or removing a member is then a deliberate action.

---

## Medium

### 8. The gateway handles plaintext passwords

Basic authentication passes the password to the process, which runs as a system service. A crash dump, a
verbose log or an exception message carrying the request is then a credential disclosure. **Never log the
header, never include the request in an error, and clear the buffer after the check.** State this in the
code at the point it applies, because a later hurried change can otherwise reintroduce it.

> **Reduced, after the design changed.** The password is no longer how a session is carried, or even how it
> begins. A client registers **once**, with a password, and from then on proves possession of a key. The
> exposure is one request in the life of a client instead of one per session, and the rules above guard a
> single endpoint that is almost never used.
>
> The gateway now holds **public keys only**, and a stolen copy of its store is worthless. **A signature is
> not sufficient**, though. The account behind a key can be disabled or dropped from the group afterwards,
> and its standing is therefore re-checked at every token issue. Without that check the key store would
> become a register of permissions kept beside Windows and slowly disagreeing with it.
>
> The cost is a private key at rest on each client, unencrypted for unattended use and protected by file
> permissions. SSH makes the same trade, and it is made here deliberately.

### 9. Token validation has a fixed list of ways to be wrong

> **Moot.** Finding 1 was resolved by removing the listener that would have verified an edge assertion, and
> there is no such token to validate. The finding is kept in case the assertion path is proposed again.

If the assertion path is built: verify the signature; check the audience is **this** application and not
merely a valid one from the same issuer; check the issuer; check expiry with a small clock skew; refuse
unsigned and `none` algorithms outright; fetch keys over TLS from the issuer and cache them, refreshing when
an unknown key id arrives instead of on every request. Skipping the audience check is the usual mistake.
Without it, any token from the same tenant, issued for any application, opens this one.

### 10. The install is an elevated script from an unsigned archive

Whoever publishes a release controls every machine that installs it. The executable is unsigned, and the
user is already being asked to click past a warning. Ask for that trust as rarely as possible: publish
checksums, keep build provenance, and never instruct users to disable a protection to complete the install.

### 11. A URL reservation granted too widely exposes the port

`netsh http add urlacl` naming a broad group lets any local user bind that prefix when the service is not
running and serve their own content on it. The reservation should name the service account alone.

### 12. Nothing records who did what

The journal carries `author`, which the client declares about itself and can set to anything. Through a
gateway this is more than untidy, because an incident cannot be reconstructed from a name the client chose
for itself.

**Fix.** The gateway logs the authenticated identity, the session, the verb and the outcome, and stamps the
identity it authenticated into the forwarded request in place of the caller's claim.

### 13. First contact is trust on first use

Pinning depends on the first connection. Someone in the path at that moment substitutes their key and is
believed from then on.

The risk is accepted, with the same mitigation SSH uses: the installer prints the fingerprint, and a user who
wants to can compare it out of band. State plainly in the documentation that the first connection is the one
that matters.

### 14. One caller can freeze a Rhino, and can exhaust the gateway

Work is queued onto Rhino's single UI thread. A caller who sends heavy verbs in a loop makes that Rhino
unusable for the person sitting in front of it.

**Downgraded on review.** Finding 3 limits a caller to the sessions they own, and a flood therefore reaches
only the flooder's own Rhino. That is a robustness problem with the caller's own script and crosses no
security boundary; it should not have been filed at this severity.

The part that survives concerns the gateway itself. A network service with no ceiling on requests in flight
can exhaust its own threads or sockets because of one bad client.

**Fix.** One cap applies across the gateway, and requests over it are refused, not queued. The protocol says
that no answer at all tells the client nothing about whether the work ran, and a silently longer wait is
worse than a refusal a client can act on.

This point was raised during review: nothing enforces one client per canvas. `docs/protocol.md` says ten
clients cost what one does, and the pairing notes tell an agent it may be waiting behind another agent.
`PHENOME_GH_PORT` pins an agent to a canvas; it does not reserve the canvas for the agent.

---

## Accepted risks

### 15. Cloudflare terminates TLS and sees the traffic

This is inherent to the arrangement, and already true of the other service this network exposes. It is a
reason to prefer the local path when both are available, and it does not argue against using the remote one.

### 16. The links themselves stay unauthenticated on loopback

Any local process that finds the port talks to a Rhino link directly, whether or not a gateway is installed.
This predates the design and the design leaves it as it is. It limits what the gateway can promise: **it
protects the machine from the network, not the machine from itself.** An authenticated gateway should not be
read as making a shared computer safe.

### 17. Code execution is the product

There is no version of this that both works and cannot run code. What can be offered is a choice: a
read-only mode that serves the reading verbs and refuses the writing ones, for the case where someone wants
to watch a canvas without being able to change it. Such a mode is worth having, and it should not be
presented as a boundary against a determined caller who also has an account.

---

## What was decided

Each finding was walked through and settled before any code exists. `link-over-the-network.md` has been
amended to match, and this table records the reason for each change.

| # | finding | decision |
|---|---|---|
| 1 | loopback listener could be left open | **the listener is gone.** A single listener requires TLS and a Windows account, and cloudflared forwards to it like any other client. A request that arrives through the tunnel is authenticated like any other |
| 2 | `/s/{port}` forges requests | the gateway issues an unguessable **handle**, and a port number never travels on the wire for a caller to substitute |
| 3 | any account reached any canvas | a caller reaches **only sessions they own** |
| 3b | ownership was self-declared | ownership is the **owner of the process behind the pid**, and the port is confirmed against the system's record of which process is listening on it |
| 4 | pooled folder leaked everyone's ports | folder grants **create-file and read-own**; the gateway reads all by system right. `/sessions` returns the caller's own in full and everyone else's as a count |
| 5 | world-writable folder in ProgramData | no delete or modify of other people's entries, reparse points refused when reading, an unparseable entry treated as absent |
| 6 | lockout as a remote off switch | the **rate limit sits in front of `LogonUser`**; the documentation recommends a purpose-made account instead of the daily one |
| 7 | any account is an account for this | membership of a **local group** created at install, alongside the password |
| 8 | plaintext passwords in a service | **key pairs, the way SSH does it.** A password registers a machine once, then the client signs a challenge and the gateway holds public keys only. The account and group are re-checked at every token issue as well as at registration |
| 9 | token validation pitfalls | moot, see above |
| 10 | unsigned executable, elevated install | **checksums and build provenance**, no certificate for now, and the warning is described honestly and not worked around |
| 11 | over-broad URL reservation | granted to the **virtual service account** alone, which is also what the service runs as |
| 12 | no record of who did what | **Windows event log**, own source: identity, session, verb, outcome, with the authenticated identity stamped into the forwarded request |
| 13 | trust on first use | accepted, handled as SSH does: show the fingerprint, ask, remember, warn loudly on change |
| 14 | one caller freezes everything | downgraded, see above. One cap on requests in flight applies across the gateway, and the excess is refused |
| 15 | Cloudflare sees the traffic | accepted, and a reason to prefer the local path where both exist |
| 16 | links unauthenticated on loopback | accepted and stated: the gateway protects the machine from the network, not the machine from itself |
| 17 | code execution is the product | accepted. A read-only mode is **not** being built now, because it would be a convenience and no boundary against a caller with an account and the group |

Findings 1, 2 and 3 corrected the design instead of hardening it. Each would have shipped as a vulnerability
had the design been built as first written.
