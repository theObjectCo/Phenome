using System.Net;
using System.Text;
using System.Text.Json;

using Phenome.Apps.GrasshopperLink.Definition;
using Phenome.Apps.GrasshopperLink.Bridge.Verbs;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// The loopback interface an agent talks to: the canvas as JSON, the journal, and a set of verbs.
/// </summary>
/// <remarks>
/// Plain HTTP on 127.0.0.1, one JSON in and one JSON out. The inspector set this convention for the same
/// reason: any client that can make a request is a peer, whether an agent's shell command, a script or a
/// webview. The port is ephemeral and written to a discovery file. Nothing is configured, and two Rhinos do not
/// collide. <c>GET /</c> describes the whole protocol, and a client needs only the discovery file's path.
/// <para>
/// Every mutation runs on the Rhino UI thread, because Grasshopper's document is a single-threaded property
/// of the window. Each one is journalled with the caller's <c>author</c>, and the user sees the agent's work.
/// </para>
/// </remarks>
internal static class LinkServer
{
    private static HttpListener? listener;

    /// <summary>The port this instance listens on, or 0 before <see cref="Start"/>.</summary>
    internal static int Port { get; private set; }

    static long served;
    static long dropped;
    static long announced;

    /// <summary>Requests answered so far. The pair button uses it to tell whether a client has ever connected.</summary>
    internal static long Served => System.Threading.Interlocked.Read(ref served);

    /// <summary>
    /// How many answers were written to a client that had already gone.
    /// </summary>
    /// <remarks>
    /// Counted, and not logged one by one. A lost answer is not a verb fault (the verb ran and did what was
    /// asked) and not a refusal, and it never enters the friction log. The command line mentions the count at
    /// 1, 10, 100 and so on. The count is a number to check when a session looked unreliable.
    /// </remarks>
    internal static long Dropped => System.Threading.Interlocked.Read(ref dropped);

    /// <summary>When the last request arrived. No recent requests means no agent is paired.</summary>
    internal static DateTime LastRequest { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// When an agent last did something, as opposed to merely being connected.
    /// </summary>
    /// <remarks>
    /// A paired client polls the journal every couple of seconds whether or not it is acting. "A request
    /// arrived" stays true for the whole connection and says nothing about work. The heartbeat and the
    /// discovery probe are excluded here for the same reason they are excluded from the command line echo:
    /// they keep the connection alive and are not actions.
    /// </remarks>
    internal static DateTime LastAction { get; private set; } = DateTime.MinValue;

    private const string Description = """
        {
          "phenome": "grasshopper-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /canvas": "the whole document: every object, wires, values, selection, enabled, preview, mapping, solver state",
            "GET /canvas?as=mermaid": "the same document as a mermaid flowchart (groups as subgraphs, red components marked) with a map of short node ids to real guids. It gives the shape of a definition at a fiftieth of the size and carries no data; branch and item counts still come from peek",
            "GET /events?since=N": "the journal after entry N; response carries 'latest' to ask from next time; a gap below the client's cursor means entries were dropped: re-read /canvas",
            "POST /dismiss": "SUPERSEDED by /dialog, and kept working. {author, button?, key?, expect?} - press a button by name, type a key, or close it when neither is given. When /pulse says clickable:false the dialog draws its own buttons and only a key reaches it: the underlined letter of the answer, or {ESC}. 'expect' names the dialog meant to be answered, and the call refuses if another one is up by then",
            "POST /dialog": "{author, button?, key?, close?, expect?} - answer the dialog Rhino is waiting on. Nothing is assumed: with no answer given this refuses and lists the buttons, and a decline is always explicit. 'close' declines. Supersedes /dismiss, which is kept for callers that already send it",
            "GET /console?tail=50": "the tail of Rhino's own command line: what commands and scripts said. It is drained when the UI thread is idle, and a long command's output arrives when it ends; /pulse covers the meantime",
            "GET /pulse": "whether Rhino is idle, busy or blocked, answered off the UI thread; it responds even when other verbs do not. 'busy' names the running command and how long it has run: wait. 'blocked' names the open dialog, which holds the UI thread until an agent answers it or the user clicks it",
            "POST /say": "{author, text, to?} - a message into the journal, for the user or another agent",
            "POST /solver": "{author, enabled} - lock or unlock the solver",
            "POST /bake": "{author, ids:[guid]} - bake those objects into the Rhino document",
            "POST /param": "{author, id, side:'input'|'output', param:nameOrIndex, mapping?:'none'|'flatten'|'graft', simplify?, reverse?} - data mapping on one parameter",
            "POST /new": "{author} - a fresh Grasshopper document on the canvas",
            "POST /open": "{author, path} - open a .gh on the canvas, or a .3dm in Rhino",
            "GET /documents": "every document Grasshopper holds open, with its id, name, path, modified flag, object count, and which one the canvas is showing. 'new' and 'open' leave the previous document open and unreachable, and more documents than intended usually accumulate",
            "POST /documents": "{author, use} - show the document with that id; every later verb then works on that one. It has the same shape as /sessions one level up: read to see what there is, send 'use' to change which one the later verbs work on",
            "POST /close": "{author, id?} - close a document, discarding whatever is unsaved in it; the one on the canvas unless another id is given. Answers what the canvas shows afterwards, and says discardedUnsavedChanges when something was thrown away. No prompt appears, because a modal would hold the UI thread every verb needs; picking this verb is the choice",
            "POST /saveandclose": "{author, id?, path?} - write the document, then close it. Without 'path' it saves where it already lives, and refuses if it has never been saved; it does not invent a location",
            "POST /add": "{author, name|guid, pivot?:[x,y], nickname?} - put a component or parameter on the canvas; answers its id",
            "POST /wire": "{author, wires:[{from:{id, param?}, to:{id, param?}, disconnect?}]} - all the wires in one call, one solution at the end. A single {from, to} at the root still works",
            "POST /set": "{author, values:[{id, value?, param?, minimum?, maximum?, decimals?, nickname?, width?, height?}]} - all the values in one call. A single one at the root still works. A slider takes bounds and precision (or a string like '0<50<100' for all three), a panel text, a toggle a flag; with 'param' the value replaces a component input's stored constant, and a null value empties it. An array stores one item per element, and [x,y,z] is a point. 'nickname' renames a parameter standing on its own (never a component), and 'width' and 'height' size a panel; with any of those, 'value' may be left out",
            "POST /select": "{author, ids:[guid], add?} - select those objects, replacing the selection unless add",
            "POST /delete": "{author, ids:[guid], force?} - remove those objects. Refuses and names the wires first if this would cut connections to objects that stay; force:true confirms the cut",
            "GET /wires": "every wire in the document, from and to, with names and parameters",
            "GET /describe?id=guid": "one placed object's parameters: names, nicknames, types, item/list access, how many wires and items each holds. A placed component needs no catalogue search",
            "POST /undo": "{author} - one step back through Grasshopper's own undo stack; every verb records into it",
            "POST /redo": "{author} - one step forward again",
            "POST /arrange": "{author} - lay the whole document out in layers, mermaid-style: sources left, few crossings, even air, and whatever feeds a component or group stacked in the order of the sockets it feeds; groups are laid out as whole blocks and their frames never overlap",
            "POST /signature": "{author, id?} - give a group (or every group) named floating parameters at its edges and re-land the crossing wires on them; the group then reads as a virtual component",
            "POST /preview": "{author, id?, on?} - quiet the preview. With no id it sweeps the document: only the outlets of the red and yellow groups keep drawing (the geometry those colours produce), and everything else goes dark, machinery and intermediates alike. Name a group instead and only that group is quieted, whatever its colour; on:true gives a group its whole preview back",
            "GET /review": "the document against the composition rules: overlapping or unnamed groups, groups doing two jobs, bare boundary crossings, ungrouped objects",
            "POST /report": "{author, expected, got, notes?} - leave a note about a verb that did not work as expected: what was expected against what happened. Refused requests are logged by themselves; this is for the rest. Local file, never sent anywhere",
            "GET /friction?tail=50": "the friction log: refused requests and reports, newest last, with the file's path",
            "POST /feedback": "{author, expected, got, to?} - assembles the whole complaint into one readable file (session, review, recent friction) and answers with its path and a mailto link. Ask the user before calling it, and let them send it: nothing is sent from here",
            "POST /group": "{author, name, ids?:[guid], colour?:[r,g,b], inlets?:[name|{name,type}], outlets?:[...]} - a named group, optionally declared signature first: inlets and outlets are created as named floating parameters and answered as a name-to-id map, and the body can then be wired onto them",
            "POST /ungroup": "{author, id} - dissolve a group, keeping its members",
            "GET /components?q=text": "search the installed component catalogue by name/description; top matches carry their inputs and outputs",
            "GET /canvas-image?width=1200&fit=true": "the Grasshopper canvas itself as PNG (base64), fitted to the whole document for the capture and the view put back after; use it to judge whether a layout reads. A size larger than the window is drawn in tiles at that size, not stretched. width and height in pixels, up to 8000 a side (one of them keeps the proportions); the picture is kept as a PNG in Pictures\\Phenome Link or in 'path' unless save=false, and 'preview=N' sends a copy at most N pixels on its long edge. The answer carries png, width, height, sent when scaled, and path",
            "GET /screenshot?width=640&zoomExtents=true": "the active Rhino viewport as PNG (base64), 640 pixels across by default, framed on the geometry for the capture and the camera put back where the user left it (zoomExtents=false skips the framing). width and height in pixels, up to 8000 a side (one of them keeps the proportions); the picture is kept as a PNG in Pictures\\Phenome Link or in 'path' unless save=false, and 'preview=N' sends a copy at most N pixels on its long edge. The answer carries png, width, height, sent when scaled, and path",
            "POST /escape": "{author, times?} - post Escape to Rhino, cancelling whatever it is waiting for. It covers the case /dismiss cannot answer: a command waiting on a pick is not a dialog. Nothing is disabled and there is no window to click, yet the UI thread is held and every verb reports 'busy' as though waiting would help. 'times' cancels that many levels; one by default",
            "GET /camera": "where the active viewport is looking: projection, camera location, target, up, 35mm lens length and the viewport's pixel size",
            "POST /camera": "{author, location?:[x,y,z], target?:[x,y,z], up?:[x,y,z], lens?, projection?:'perspective'|'parallel'} - aim the active viewport. Only the fields passed change. This is how to frame a particular view: the Zoom command is interactive, and a scripted one waits for a pick that never comes. That hangs the UI thread and every other verb with it",
            "GET /peek?id=guid&side=input|output&param=nameOrIndex": "the full data on one parameter, branch by branch with tree paths. Give a group's id instead and it answers that group's signature as it stands: every inlet and outlet with its type, branch and item counts, and a few values off each outlet",
            "GET /measure?id=guid&side=output&param=nameOrIndex&against=guid&againstSide=&againstParam=": "lengths, areas and volumes of the geometry on one parameter (an output unless side=input), item by item with tree paths, and their totals and bounding box. With 'against' every pair from the two sets is compared: the area two closed planar curves share, the volume two solids share, and the nearest distance between curves or points. The same id and parameter twice compares the set with itself, each pair once",
            "GET /rhino": "the Rhino document: name, layers, object count",
            "GET /plugins": "what is loaded: Grasshopper libraries and loaded Rhino plug-ins, each with version and the file it came from. Use when the console output points to a plug-in rather than a component",
            "POST /place": "{author, group?, objects:[{id?, name|guid, nickname?, pivot?, slider?, text?, value?, inputs?:[{param?, sources:[{id, output?}]}]}]} - a whole recipe in one call; local ids wire to each other and to existing canvas guids, 'group' puts everything placed into that group; answers the id map",
            "POST /save": "{author, path?} - save the document (autosave also runs once before an agent's first edit)",
            "POST /zoom": "{author, ids:[guid]} - focus the canvas view on those objects",
            "POST /rhino": "{author, script} - run a Rhino command script (layers, blocks, groups: the whole command language)",
            "GET /scripts": "the script components on the canvas, with their generation",
            "GET /script?id=guid": "one script component's source",
            "POST /script": "{author, id, source} - new source in, one solve, the component's errors and warnings back",
            "POST /pillscript": "{author, tool, arguments?} - a PillScript component, through PillScript itself: list_components, list_files, read_file, write_file, delete_file, rename_file, list_references, add_package, remove_package, add_reference, remove_reference, compile, solve, open_editor. 'arguments' is the tool's own object, and 'component' in it names the component by id or a unique prefix of it. Needs PillScript 0.5.0 or later loaded in the same Rhino"
          },
          "discovery": "%TEMP%/phenome-link-<rhino pid>.port holds this port; no file, no session"
        }
        """;

    /// <summary>Binds an ephemeral loopback port and starts answering.</summary>
    internal static void Start()
    {
        // Started before the listener: the first request already sees what Rhino is doing and what it has said.
        Pulse.Start();
        CommandLine.Start();

        // Started here and not on the first request: the agent-driving border covers the first request too.
        Attention.Start();

        listener = Listen();

        Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();

                    System.Threading.Interlocked.Increment(ref served);
                    LastRequest = DateTime.Now;

                    // Answered on its own thread: a long verb cannot block the accept loop. Run inline, a
                    // two-minute bake holds the loop for two minutes, and a second client is not queued behind
                    // it. That client is never accepted at all, and its own timeout fires. In one session with
                    // two agents on one canvas, 947 of 1132 friction entries were "the specified network name
                    // is no longer available", each one a client giving up on being accepted.
                    //
                    // This is safe because no document work happens here. Every verb marshals onto the UI
                    // thread through OnUi: document access stays serialised, and one OnUi block runs to
                    // completion before another starts. Only work that never needed Rhino runs in parallel:
                    // parsing, the journal and friction behind their locks, and writing the answer.
                    _ = Task.Run(() => Answer(context));
                }
                catch (Exception) when (!listener.IsListening)
                {
                    // The listener was shut down mid-await, which is expected.
                }
                catch (Exception failure)
                {
                    LinkLog.Say($"Phenome Link: a request failed. {failure.Message}");
                }
            }
        });
    }

    private static void Answer(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string method = context.Request.HttpMethod;

        // Checked before anything is read or run: a browser must not be able to drive this, and this API
        // compiles C#. Loopback is not a boundary against a page the user merely visits (see Browser.Refuse).
        if (Browser.Refuse(context.Request, Port) is { } refused)
        {
            Send(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(refused)}}}");
            return;
        }

        // A withdrawn version keeps answering the greeting and refuses the verbs. With the listener torn
        // down, a client would see a dead port and report "no session", and the user would never learn why.
        // A refusal carries the notice text and tells the client at the other end, person or agent, what to
        // do.
        if (Advisory.Withdrawn is { } notice && path.Length != 0)
        {
            Send(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(notice.Sentence)}}}");
            return;
        }

        // Read once, up front: the body stream is single-pass, and a refusal can only quote the request if
        // the request was kept.
        string payload = method == "POST" ? ReadBody(context.Request) : "";

        if (path != "/events" && path.Length != 0)
        {
            LastAction = DateTime.Now;
        }

        System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            string body = (method, path) switch
            {
                ("GET", "") => Description,
                ("GET", "/canvas") when context.Request.QueryString["as"] == "mermaid" =>
                    OnUi(() => CanvasWriter.Mermaid(ActiveDocument())),
                ("GET", "/canvas") => Json.Indented(OnUi(() => CanvasWriter.Write(ActiveDocument()))),
                ("GET", "/events") => Journal.Since(Since(context.Request)),
                ("GET", "/pulse") => Pulse.Report(),
                ("POST", "/dismiss") => Process.Dismissed(Read(payload)),
                ("POST", "/dialog") => Process.AnswerDialog(Read(payload)),
                ("POST", "/escape") => Process.Escaped(Read(payload)),
                ("GET", "/console") => CommandLine.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int back) ? Math.Clamp(back, 1, 500) : 50,
                    string.Equals(context.Request.QueryString["mine"], "true", StringComparison.OrdinalIgnoreCase)),
                ("POST", "/say") => Process.Say(Read(payload)),
                ("POST", "/solver") => Documents.Solver(Read(payload)),
                ("POST", "/bake") => Documents.Bake(Read(payload)),
                ("POST", "/param") => Objects.Mapping(Read(payload)),
                ("POST", "/new") => Documents.NewDocument(Read(payload)),
                ("POST", "/open") => Documents.Open(Read(payload)),
                ("GET", "/documents") => Documents.Opened(),
                ("POST", "/documents") => Documents.Use(Read(payload)),
                ("POST", "/close") => Documents.Close(Read(payload), saveFirst: false),
                ("POST", "/saveandclose") => Documents.Close(Read(payload), saveFirst: true),
                ("POST", "/add") => Objects.Add(Read(payload)),
                ("POST", "/wire") => Objects.Wire(Read(payload)),
                ("POST", "/set") => Objects.SetValue(Read(payload)),
                ("POST", "/select") => Objects.Select(Read(payload)),
                ("POST", "/delete") => Objects.Delete(Read(payload)),
                ("GET", "/wires") => OnUi(Reading.Wires),
                ("GET", "/describe") => OnUi(() => Reading.Describe(
                    Guid.Parse(context.Request.QueryString["id"]
                        ?? throw new ArgumentException("describe needs ?id=guid.")))),
                ("POST", "/undo") => Documents.Undo(Read(payload), forward: false),
                ("POST", "/redo") => Documents.Undo(Read(payload), forward: true),
                ("POST", "/arrange") => Groups.DoArrange(Read(payload)),
                ("POST", "/signature") => Groups.DoSignature(Read(payload)),
                ("POST", "/preview") => View.Quiet(Read(payload)),
                ("GET", "/review") => OnUi(() => Review.Whole(ActiveDocument())),
                ("POST", "/group") => Groups.Group(Read(payload)),
                ("POST", "/ungroup") => Groups.Ungroup(Read(payload)),
                ("GET", "/components") => OnUi(() => Catalogue.Search(
                    context.Request.QueryString["q"]
                        ?? throw new ArgumentException("components needs ?q=text."))),
                ("GET", "/screenshot") => View.Screenshot(context.Request),
                ("GET", "/camera") => OnUi(View.ReadCamera),
                ("POST", "/camera") => View.AimCamera(Read(payload)),
                ("GET", "/canvas-image") => View.CanvasImage(context.Request),
                ("GET", "/peek") => Reading.Peek(context.Request),
                ("GET", "/measure") => Measure.Answer(context.Request),
                ("GET", "/rhino") => OnUi(Reading.RhinoSummary),
                ("GET", "/plugins") => OnUi(Reading.Plugins),
                ("POST", "/place") => Objects.Place(Read(payload)),
                ("POST", "/save") => Documents.Save(Read(payload)),
                ("POST", "/zoom") => View.Zoom(Read(payload)),
                ("POST", "/rhino") => Documents.RunScript(Read(payload)),
                ("GET", "/scripts") => OnUi(() => Scripts.List(ActiveDocument())),
                ("GET", "/script") => OnUi(() => Scripts.Read(
                    ActiveDocument(),
                    Guid.Parse(context.Request.QueryString["id"]
                        ?? throw new ArgumentException("script needs ?id=guid.")))),
                ("POST", "/script") => Documents.WriteScript(Read(payload)),
                ("POST", "/pillscript") => PillScripts.Run(Read(payload)),
                ("POST", "/report") => Process.Reported(Read(payload)),
                ("POST", "/feedback") => Process.Feedback(Read(payload)),
                ("GET", "/friction") => Friction.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int tail) ? Math.Clamp(tail, 1, 500) : 50),
                _ => throw new KeyNotFoundException($"There is no {method} {path}. GET / describes what there is."),
            };

            Echo(method, path, ok: true, said: null, clock);
            Send(context.Response, 200, Door(context.Request, method, path, payload, body));
        }
        catch (KeyNotFoundException missing)
        {
            Refuse(context, 404, method, path, payload, missing, clock);
        }
        catch (Exception failure)
        {
            Refuse(context, 500, method, path, payload, failure, clock);
        }
    }

    /// <summary>The authors already told that the MCP tools exist.</summary>
    private static readonly HashSet<string> Told = [];

    /// <summary>
    /// Verbs that change nothing on the canvas. A script sending one of these gets no door sentence.
    /// </summary>
    private static readonly HashSet<string> Talk =
        ["/say", "/report", "/feedback", "/dialog", "/dismiss", "/escape", "/select", "/zoom"];

    /// <summary>
    /// The first edit an author sends over plain HTTP gets one sentence pointing at the MCP tools.
    /// </summary>
    /// <remarks>
    /// An agent that loses its <c>phenome</c> tools can fall back to plain HTTP scripts and never notice when
    /// the tools return, because a script and an MCP call to the same verb get identical answers. The two
    /// clients this repository ships identify themselves in <c>X-Phenome-Client</c>. A request without the
    /// header comes from a script, curl or another machine.
    /// <para>
    /// Sent once per author for the life of this Rhino, and only on a verb that changes the canvas. A loop of
    /// <c>set</c> over variants is a legitimate use of a script, and a sentence on every answer would bury it.
    /// </para>
    /// </remarks>
    private static string Door(HttpListenerRequest request, string method, string path, string payload, string body)
    {
        if (method != "POST"
            || !string.IsNullOrEmpty(request.Headers["X-Phenome-Client"])
            || Talk.Contains(path)
            || body.Length < 3
            || body[0] != '{'
            || body[^1] != '}')
        {
            return body;
        }

        string author;

        try
        {
            author = Author(Read(payload));
        }
        catch (JsonException)
        {
            author = "unnamed";
        }

        lock (Told)
        {
            if (!Told.Add(author))
            {
                return body;
            }
        }

        const string Sentence =
            "This edit came over plain HTTP, not through the phenome MCP tools. If the agent's MCP host lists tools "
            + "named mcp__phenome__* (Claude Code may list them as deferred, and one ToolSearch loads them), "
            + "build and change the definition through them. A script suits a loop of set, peek and measure. "
            + "This note is sent once per author.";

        return body[..^1] + ",\"door\":" + Json.Quote(Sentence) + "}";
    }

    /// <summary>Records a refusal, says it, and sends it.</summary>
    private static void Refuse(
        HttpListenerContext context,
        int status,
        string method,
        string path,
        string payload,
        Exception failure,
        System.Diagnostics.Stopwatch clock)
    {
        Friction.Refused($"{method} {path}", payload, failure.Message);
        Echo(method, path, ok: false, said: failure.Message, clock);
        Send(context.Response, status, $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}");
    }

    /// <summary>
    /// Writes the answer. A failed write is handled apart from a failed verb.
    /// </summary>
    /// <remarks>
    /// <see cref="Send"/> catches its own write failures although it remains inside the verb's try block. If
    /// the write shared the verb's exception handling, a client that had gone would make
    /// <see cref="Respond"/> throw, and the general catch would treat it as the verb failing. The friction log
    /// would gain entries for verbs that had in fact run, the command line would echo a failure that had not
    /// happened, and the catch would call <see cref="Respond"/> a second time on a closed stream ("this
    /// operation cannot be performed after the response has been submitted"). In one two-agent session that
    /// accounted for 947 of 1132 friction entries.
    /// <para>
    /// Writing is the last step and nothing follows it. A failure here is counted and otherwise ignored: the
    /// verb already ran, and there is no client left to tell.
    /// </para>
    /// <para>
    /// The caller's side stays as it is: an agent seeing a transport error still cannot tell whether the verb
    /// ran, and must not retry a mutating verb on that error. Because the journal carries the author, reading
    /// <c>/events</c> back and looking for its own entry tells the agent whether the work landed. Retrying
    /// blind applies a non-idempotent verb twice.
    /// </para>
    /// </remarks>
    private static void Send(HttpListenerResponse response, int status, string body)
    {
        try
        {
            Respond(response, status, body);
        }
        catch (Exception failure)
        {
            long count = System.Threading.Interlocked.Increment(ref dropped);

            // Announced at 1, 10, 100 and so on. A session that loses one answer reports it once, and one
            // losing them steadily reports it a few times, not a thousand.
            long at = System.Threading.Interlocked.Read(ref announced);

            if (count >= NextAnnouncement(at))
            {
                System.Threading.Interlocked.Increment(ref announced);

                LinkLog.Say(
                    $"Phenome Link: {count} answer(s) could not be delivered: the client had gone. " +
                    $"The verbs themselves ran. Latest: {failure.Message}");
            }
        }
    }

    /// <summary>The count at which the next message is due: 1, 10, 100, 1000 and so on.</summary>
    private static long NextAnnouncement(long already) =>
        already switch { 0 => 1, 1 => 10, 2 => 100, _ => (long)Math.Pow(10, already) };

    /// <summary>
    /// One line per request in Rhino's own command line: the time, the verb, and whether it worked.
    /// </summary>
    /// <remarks>
    /// The journal and the VS Code channel are the full record. This line is for the person watching an agent
    /// work from inside Rhino: it shows that the agent is doing something and where it stopped, with nothing
    /// else to look at. It is queued onto the UI thread, because requests are answered on workers.
    /// </remarks>
    private static void Echo(string method, string path, bool ok, string? said, System.Diagnostics.Stopwatch? clock = null)
    {
        // The heartbeat and the discovery probe are not echoed. A client polls the journal every couple of
        // seconds, and echoing each poll buries the line the watcher wants under hundreds that report
        // nothing. Failures are echoed whatever the path.
        if (ok && (path == "/events" || path.Length == 0))
        {
            return;
        }

        // Three bracketed fields and then the verb: when, from where, how long, what. The log is read down
        // the columns, and the time and address fields are a fixed width. The brackets make that visible: a
        // column with drawn edges cannot drift by a character without showing it.
        //
        // The duration is not padded. Padding to a fixed width makes a slow call findable by shape in a column
        // of four-digit numbers. In this log nearly every line has two digits of milliseconds, and the padding
        // reads as a gutter. The brackets already mark a slow call: an eye finds [1.4 s] among [78 ms]
        // unaided. The cost is that the verb does not start at a fixed column, the cheaper loss because the
        // verb is what the reader scans for.
        //
        // No line prints "ok". A column of identical words carries no information and would be the widest
        // field on the line; only the failure lines are marked.
        //
        // The address is on every line although it never changes within a Rhino. A banner written once at
        // load scrolls off the top within about fifteen requests, and the address is the fact a reader needs
        // to hand the session to an agent or to tell two Rhinos apart. On every line, it also names the
        // session in any screenshot.
        //
        // The line carries the whole address: 127.0.0.1:53654 pastes straight into a request, and 53654 alone
        // has to be assembled first. The address comes from the same constant the listener binds, and the log
        // cannot name an address nothing is listening on.
        //
        // The verb goes last, after the bracketed fields: it is the only part whose width varies and the only
        // part a reader scans for. Anything variable in the middle pushes every later column out of line,
        // which is what the brackets exist to prevent.
        string verb = path.Length == 0 ? "/" : path.TrimStart('/');
        (string amount, string unit) = clock is null ? ("", "") : Duration(clock.ElapsedMilliseconds);

        string line = string.Format(
            "[{0}] [{1,-15}] [{2} {3}] {4}{5}",
            DateTime.Now.ToString("HH:mm:ss"),
            $"{Loopback.Address}:{Port}",
            amount,
            unit,
            verb,
            ok || string.IsNullOrEmpty(said) ? "" : "  !!  " + OneLine(said));

        line = line.TrimEnd();

        // Claimed before writing: capture cannot tell this plugin's echo from Rhino's own output, and an agent
        // reading /console must not be shown its own requests as news.
        CommandLine.Ours(line);

        try
        {
            Rhino.RhinoApp.InvokeOnUiThread(() => Rhino.RhinoApp.WriteLine(line));
        }
        catch (Exception)
        {
            // A log line that cannot be written is dropped without an error.
        }
    }

    /// <summary>
    /// A duration in a unit that reads at a glance, in place of four digits of milliseconds. Amount and unit are
    /// returned apart; the caller places them in separate columns and the digits stay aligned.
    /// </summary>
    private static (string Amount, string Unit) Duration(long milliseconds) =>
        milliseconds < 1000
            ? ($"{milliseconds}", "ms")
            : milliseconds < 60_000
                ? ($"{milliseconds / 1000.0:0.0}", "s")
                : ($"{milliseconds / 60_000}m{milliseconds % 60_000 / 1000:00}", "s");

    /// <summary>
    /// A message collapsed to one line and shortened to what the command line can show. A refusal wrapping
    /// over four lines would push the earlier echo lines off the top.
    /// </summary>
    private static string OneLine(string said)
    {
        string flat = said.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");

        while (flat.Contains("  "))
        {
            flat = flat.Replace("  ", " ");
        }

        flat = flat.Trim();

        return flat.Length <= 96 ? flat : flat.Substring(0, 93) + "...";
    }

    private static void Respond(HttpListenerResponse response, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.ContentType = "application/json";

        // No Access-Control-Allow-Origin header is sent, deliberately. The listener never leaves loopback,
        // but a page the user visits can reach loopback, and with "*" in that header the page could read the
        // answers. It could then knock on ports until one said grasshopper-link. Without the header a browser
        // gets an opaque response and learns nothing, and this link is not found at all where it would
        // otherwise be found in a minute.
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }

    // ---- The verbs -------------------------------------------------------------------------------------

    /// <summary>
    /// Binds a listener on an ephemeral loopback port and publishes the port it settled on.
    /// </summary>
    /// <remarks>
    /// The retrying lives in <see cref="Loopback.Listen"/>, shared with the Rhino half. What stays here is
    /// this class's own concern: <see cref="Port"/> is assigned from the out parameter and only ever holds a
    /// port a listener is actually running on.
    /// </remarks>
    private static HttpListener Listen()
    {
        HttpListener listening = Loopback.Listen(out int port);

        Port = port;

        return listening;
    }
}
