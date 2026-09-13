# The gateway, read adversarially

An audit of the architecture in `link-over-the-network.md`, written before any of it exists, which is the
cheapest moment to find out that three of the decisions are wrong.

## What is actually being protected

Not a canvas. **The link is remote code execution by design.** `script_write` compiles and runs C# inside
Rhino, `rhino_command` types anything at the command line, and `/load` loads a plug-in. Anyone who reaches
a link runs code as the user whose Rhino it is.

So the value at stake is the machine and the account, not the definition on screen. Every finding below is
weighed against that, and it is why "it is only a Grasshopper plugin" is not an argument anywhere in this
document.

Secondary assets, in order: the Windows account password that the network listener is handed; the private
key the gateway binds; the definitions themselves, which are somebody's intellectual property; and the
session inventory, which tells an attacker what is worth attacking.

## The boundaries, and what each one is worth

| boundary | trusted because | if it fails |
|---|---|---|
| internet to Cloudflare | the owner configured a policy | anything on the internet reaches the tunnel |
| Cloudflare to host | the tunnel was dialled outbound by the host | Cloudflare sees plaintext, which is accepted |
| cloudflared to gateway | both are on the host | **any local process can pretend to be cloudflared** |
| network client to gateway | TLS, pinned key, a Windows account | the account is the machine |
| gateway to a Rhino link | loopback | unauthenticated, and always has been |
| Rhino to the session folder | a filesystem ACL | a spoofed entry redirects the gateway |
| installer to machine | it was run with administrator rights | the machine, entirely |

---

## Critical

### 1. The loopback listener must fail closed, and as written it does not

The design says the loopback listener takes "optionally a signed assertion from the edge". Optional means a
default, and a default means somebody installs the gateway, points a tunnel at it, does not configure a
verifier, and **publishes remote code execution to the internet**. Nothing breaks visibly. It simply works,
for everybody.

This is the same ordering hazard that makes a raw TCP tunnel dangerous: with an HTTP application you would
notice the missing login screen, but a proxy that answers correctly gives no sign that nobody is being
asked anything.

**Fix.** The listener does not start unless a verifier is configured. No implicit trust in "it came from
loopback, so it must be cloudflared". If the operator wants no verifier, that is a flag they set by hand,
named for what it does, and the service says so in its log at every start.

### 2. `/s/{port}{verb}` is a request-forgery primitive

The caller chooses the port. Unrestricted, an authenticated user makes a SYSTEM process connect to **any**
loopback port on the host and relay their body to it. On the machine in question that includes Rhino
Compute on 5000, anything else bound to loopback, and whatever a future service parks there. A loopback-only
service is often written on the assumption that only a local process can reach it, which is exactly the
assumption this breaks.

**Fix.** The port is not a parameter, it is a selector. The gateway resolves it against the sessions it
currently knows, and refuses anything else. A port that is not in the inventory is a `404`, never a
connection attempt.

### 3. Authentication without authorisation: any account reaches any canvas

The design proves who is calling and then stops. It never says which sessions that identity may touch, so
an authenticated user drives every Rhino on the machine, including other people's.

This is worse than it looks on a machine several people use, and it is precisely the kind of machine that
gets a gateway installed on it.

**And the obvious fix has a trap.** The session file is written by the plugin, running as the user, so its
`user` and `session` fields are **self-declared**. A local attacker writes a file claiming any identity they
like and points it at a port they control.

**Fix.** Two parts. Authorise by comparing the caller's identity with the session's owner. Establish that
owner from something the writer does not control: the **file's owner on disk**, or the owner of the process
behind the pid. The fields inside the file are a convenience for display, never an input to a decision.

---

## High

### 4. One folder for everyone leaks everyone's ports

Per-user temp directories were an accidental boundary, and moving to a machine-wide folder removes it. Any
local user can now read the inventory of every other user's live links and connect to them directly on
loopback, with no gateway involved and nothing in the way.

This is a regression introduced by the change, not a pre-existing condition, and it should be recorded as
one. It does not make the links reachable where they were not - a loopback port scan finds them anyway -
but it turns a search into a directory listing.

**Fix.** The folder grants create-file, and read only to the owner. The gateway runs with system rights and
reads everything regardless. A local client reads its own entries, which is all it ever needed.

### 5. A world-writable folder in ProgramData is a known shape of vulnerability

Letting every user create files somewhere every user can write invites the classics: squatting a name
before the legitimate writer gets there, replacing an entry, and junctions or symlinks pointing the reader
somewhere else entirely.

**Fix.** Grant create-file without delete or modify of other people's entries, deny traversal tricks by
refusing reparse points when reading, and treat a malformed or unexpected entry as absent rather than
trying to interpret it.

### 6. Account lockout turns the login into a remote off switch

Every failed `LogonUser` counts against the account's lockout policy. Anyone who can reach the network
listener can lock a person out of their own Windows account by trying a few wrong passwords, without ever
getting in. Denial of service that needs no skill and leaves the victim unable to log on at the console
either.

**Fix.** Rate-limit by source and refuse early, so attempts never reach `LogonUser` fast enough to matter.
Consider validating against a purpose-made local account rather than the person's daily one, which keeps a
lockout away from the account they need to actually use the computer.

### 7. Any account on the machine is an account for this

"A Windows account" includes service accounts, a re-enabled Guest, and every low-privilege account someone
created years ago. Authentication says who; it does not say that this person was ever meant to drive Rhino.

**Fix.** Membership of a local group created at install is the condition, not the mere existence of an
account. Adding somebody is then an act, and removing them is one too.

---

## Medium

### 8. The gateway handles plaintext passwords

Basic hands the password to the process. It runs as a system service, so a crash dump, a verbose log, or an
exception message carrying the request is a credential disclosure. **Never log the header, never include
the request in an error, and clear the buffer after the check.** Say it in the code, at the place it
matters, because this is a thing that gets added back by a later change made in a hurry.

> **Reduced, after the design changed.** The password is no longer how a session is carried, or even how it
> begins. A client registers **once**, with a password, and from then on proves a key. So the exposure is
> one request in the life of a client rather than one per session, and the rules above guard a single
> endpoint that is used almost never.
>
> Two things come with that and are worth naming. The gateway now holds **public keys only**, so its store
> is not a thing worth stealing. And **a signature is not sufficient**: the account behind a key can be
> disabled or dropped from the group afterwards, so its standing is re-checked at every token issue, or
> this becomes a register of permissions living beside Windows and slowly disagreeing with it.
>
> The cost is a private key at rest on each client, unencrypted for unattended use, protected by file
> permissions. The same trade SSH makes, made deliberately.

### 9. Token validation has a fixed list of ways to be wrong

> **Moot.** Finding 1 was resolved by removing the listener that would have verified an edge assertion, so
> there is no such token to validate. Kept for the day somebody proposes bringing it back.


If the assertion path is built: verify the signature; check the audience is **this** application and not
merely a valid one from the same issuer; check the issuer; check expiry with a small clock skew; refuse
unsigned and `none` algorithms outright; fetch keys over TLS from the issuer and cache them, refreshing on
an unknown key id rather than on every request. Skipping the audience check is the usual mistake and it
means any token from the same tenant, for any application, opens this one.

### 10. The install is an elevated script from an unsigned archive

Whoever can publish a release owns every machine that installs one, and the executable is unsigned, so the
user is already being asked to click past a warning. Ask for that trust as rarely as possible: publish
checksums, keep build provenance, and never tell people to disable a protection to get through the install.

### 11. A URL reservation granted too widely hands over the port

`netsh http add urlacl` naming a broad group lets any local user bind that prefix when the service is not
running, and serve their own thing on it. Grant it to the service account alone.

### 12. Nothing records who did what

The journal carries `author`, which the client declares about itself and can say anything it likes. Over a
gateway that is no longer merely untidy: it is the difference between an incident that can be reconstructed
and one that cannot.

**Fix.** The gateway logs the authenticated identity, the session, the verb and the outcome, and stamps the
identity it authenticated into the forwarded request rather than passing the caller's claim through.

### 13. First contact is trust on first use

Pinning is only as good as the first connection. Someone in the path at that moment substitutes their key
and is believed from then on.

Accepted, with the same mitigation SSH has: the installer prints the fingerprint, so it can be compared out
of band by whoever cares. Say plainly in the documentation that the first connection is the one that
matters.

### 14. One caller can freeze a Rhino, and can exhaust the gateway

Work is queued onto Rhino's single UI thread, so a caller who sends heavy verbs in a loop makes that Rhino
unusable for the person sitting in front of it.

**Downgraded on review.** Finding 3 limits a caller to the sessions they own, so a flood reaches only the
flooder's own Rhino. That is a robustness problem with somebody's own script, not a security boundary, and
it should not have been filed at this severity.

What does survive is about the gateway rather than about Rhino: a network service with no ceiling on
requests in flight is one bad client away from exhausting its own threads or sockets.

**Fix.** One cap across the gateway, excess refused. Not a queue: the protocol says no answer at all tells
you nothing about whether the work ran, so silently lengthening a wait is worse than a refusal a client can
act on.

A note on the premise, because it came up: nothing enforces one client per canvas. `docs/protocol.md` says
ten clients cost what one does, and the pairing notes tell an agent it may be waiting behind another agent.
`PHENOME_GH_PORT` pins an agent to a canvas; it does not reserve the canvas for the agent.

---

## Accepted, and worth writing down as accepted

### 15. Cloudflare terminates TLS and sees the traffic

Inherent to the arrangement, and already true of the other service this network exposes. It is a reason to
prefer the local path when both are available, not a reason to avoid the remote one.

### 16. The links themselves stay unauthenticated on loopback

Any local process that finds the port talks to a Rhino link directly, gateway or no gateway. This predates
the design and the design does not change it, but it bounds what the gateway can promise: **it protects the
machine from the network, not the machine from itself.** Nobody should read an authenticated gateway as
making a shared computer safe.

### 17. Code execution is the product

There is no version of this that both works and cannot run code. What can be offered is a choice: a
read-only mode that serves the reading verbs and refuses the writing ones, for the cases where someone
wants to watch a canvas without being able to change it. Worth having, not worth pretending is a boundary
against a determined caller who also has an account.

---

## What was decided

Every finding was walked through and settled before any code exists. `link-over-the-network.md` has been
amended to match; this table is the record of why it says what it says.

| # | finding | decision |
|---|---|---|
| 1 | loopback listener could be left open | **the listener is gone.** One listener, TLS and a Windows account, and cloudflared forwards to it like any other client. Arriving down a tunnel is a way of arriving, not a reason to be believed |
| 2 | `/s/{port}` forges requests | the gateway issues an unguessable **handle**; a port number never travels on the wire, so there is nothing to substitute |
| 3 | any account reached any canvas | a caller reaches **only sessions they own** |
| 3b | ownership was self-declared | ownership is the **owner of the process behind the pid**, and the port is confirmed against the system's record of which process is listening on it |
| 4 | pooled folder leaked everyone's ports | folder grants **create-file and read-own**; the gateway reads all by system right. `/sessions` returns the caller's own in full and everyone else's as a count |
| 5 | world-writable folder in ProgramData | no delete or modify of other people's entries, reparse points refused when reading, an unparseable entry treated as absent |
| 6 | lockout as a remote off switch | the **rate limit sits in front of `LogonUser`**; the documentation recommends a purpose-made account rather than the daily one |
| 7 | any account is an account for this | membership of a **local group** created at install, alongside the password |
| 8 | plaintext passwords in a service | **key pairs, the way SSH does it.** A password registers a machine once, then the client signs a challenge and the gateway holds public keys only. Account and group re-checked at every token issue, not just at registration |
| 9 | token validation pitfalls | moot, see above |
| 10 | unsigned executable, elevated install | **checksums and build provenance**, no certificate for now, and the warning is described honestly rather than worked around |
| 11 | over-broad URL reservation | granted to the **virtual service account** alone, which is also what the service runs as |
| 12 | no record of who did what | **Windows event log**, own source: identity, session, verb, outcome, with the authenticated identity stamped into the forwarded request |
| 13 | trust on first use | accepted, handled as SSH does: show the fingerprint, ask, remember, warn loudly on change |
| 14 | one caller freezes everything | downgraded, see above. One cap on requests in flight across the gateway, excess refused |
| 15 | Cloudflare sees the traffic | accepted, and a reason to prefer the local path where both exist |
| 16 | links unauthenticated on loopback | accepted and stated: the gateway protects the machine from the network, not the machine from itself |
| 17 | code execution is the product | accepted. A read-only mode is **not** being built now: it is a convenience, not a boundary against somebody with an account and the group |

Three of these were corrections rather than hardening - 1, 2 and 3 - and each of them would have shipped as
a hole had the design been built as first written.
