using System.Net;
using System.Text;
using System.Text.Json;

using Phenome.Apps.GrasshopperLink.Definition;
using Phenome.Apps.GrasshopperLink.Bridge.Verbs;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// The loopback interface an agent talks to: the canvas as JSON, the journal, and a handful of verbs.
/// </summary>
/// <remarks>
/// Plain HTTP on 127.0.0.1, one JSON in, one JSON out - the same convention the inspector set, chosen for
/// the same reason: any client that can make a request is a peer, whether it is an agent's shell command, a
/// script or one day a webview. The port is ephemeral and written to a discovery file, so nothing is
/// configured and two Rhinos do not fight. <c>GET /</c> describes the whole protocol, so a client needs to
/// know nothing but the discovery file's path.
/// <para>
/// Every mutation runs on the Rhino UI thread - Grasshopper's document is single-threaded property of the
/// window - and is journalled with the caller's <c>author</c>, so the human sees the agent's hands move.
/// </para>
/// </remarks>
internal static class LinkServer
{
    private static HttpListener? listener;

    /// <summary>The port this instance listens on, or 0 before <see cref="Start"/>.</summary>
    internal static int Port { get; private set; }

    /// <summary>How many requests have been answered - the "has anyone ever connected" the pair button reads.</summary>
    internal static long Served { get; private set; }

    /// <summary>When the last request came in - a quiet line means nobody is paired.</summary>
    internal static DateTime LastRequest { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// When an agent last did something, as opposed to merely being connected.
    /// </summary>
    /// <remarks>
    /// A paired client polls the journal every couple of seconds whether or not anything is happening,
    /// so "a request arrived" is true for as long as anybody is attached and says nothing about whether
    /// they are working. The heartbeat and the discovery probe are excluded here for the same reason
    /// they are excluded from the command line echo: they are the connection breathing, not an act.
    /// </remarks>
    internal static DateTime LastAction { get; private set; } = DateTime.MinValue;

    private const string Description = """
        {
          "phenome": "grasshopper-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /canvas": "the whole document: every object, wires, values, selection, enabled, preview, mapping, solver state",
            "GET /canvas?as=mermaid": "the same document as a mermaid flowchart - groups as subgraphs, red components marked - with a map of short node ids to real guids. The shape of a definition at a fiftieth of the size; it carries no data, so branch and item counts still come from peek",
            "GET /events?since=N": "the journal after entry N; response carries 'latest' to ask from next time; a gap below your cursor means entries were dropped - re-read /canvas",
            "POST /dismiss": "{author, button?, key?, expect?} - answer the dialog Rhino is waiting on: press a button by name, type a key, or close it when neither is given. When /pulse says clickable:false the dialog draws its own buttons and only a key reaches it - the underlined letter of the answer, or {ESC}. 'expect' names the dialog you meant to answer and refuses if another one is up by then",
            "GET /console?tail=50": "the tail of Rhino's own command line - what commands and scripts said, which until now went only to the human. Drained when the UI thread breathes, so a long command's output arrives when it ends; /pulse is the verb for the meantime",
            "GET /pulse": "whether Rhino is idle, busy or blocked - answered without the UI thread, so it still answers when nothing else does. 'busy' names the running command and how long it has run: wait. 'blocked' names the open dialog: nothing will answer until somebody clicks it",
            "POST /say": "{author, text, to?} - a message into the journal, for whoever reads it",
            "POST /solver": "{author, enabled} - lock or unlock the solver",
            "POST /bake": "{author, ids:[guid]} - bake those objects into the Rhino document",
            "POST /param": "{author, id, side:'input'|'output', param:nameOrIndex, mapping?:'none'|'flatten'|'graft', simplify?, reverse?} - data mapping on one parameter",
            "POST /new": "{author} - a fresh Grasshopper document on the canvas",
            "POST /open": "{author, path} - open a .gh on the canvas, or a .3dm in Rhino",
            "POST /add": "{author, name|guid, pivot?:[x,y], nickname?} - put a component or parameter on the canvas; answers its id",
            "POST /wire": "{author, wires:[{from:{id, param?}, to:{id, param?}, disconnect?}]} - all the wires in one call, one solution at the end. A single {from, to} at the root still works",
            "POST /set": "{author, values:[{id, value, param?, minimum?, maximum?, decimals?}]} - all the values in one call. A single one at the root still works. A slider takes bounds and precision (or a string like '0<50<100' for all three), a panel text, a toggle a flag; with 'param' the value replaces a component input's stored constant, and a null value empties it",
            "POST /select": "{author, ids:[guid], add?} - select those objects, replacing the selection unless add",
            "POST /delete": "{author, ids:[guid], force?} - remove those objects. Refuses and names the wires first if this would cut connections to objects that stay; force:true means you meant it",
            "GET /wires": "every wire in the document, from and to, with names and parameters - the whole picture no per-input peek adds up to",
            "GET /describe?id=guid": "one placed object's parameters: names, nicknames, types, item/list access, how many wires and items each holds - so a placed component needs no catalogue search",
            "POST /undo": "{author} - one step back through Grasshopper's own undo stack; every verb records into it",
            "POST /redo": "{author} - one step forward again",
            "POST /arrange": "{author} - lay the whole document out in layers, mermaid-style: sources left, few crossings, even air; groups are laid out as whole blocks, so their frames never overlap",
            "POST /signature": "{author, id?} - give a group (or every group) named floating parameters at its edges and re-land the crossing wires on them, so it reads as a virtual component",
            "POST /preview": "{author, id?, on?} - quiet the preview. With no id it sweeps the document: only the outlets of the red and yellow groups keep drawing - the geometry those colours promised - and everything else goes dark, machinery and intermediates alike. Name a group instead and that one is quieted on its own terms, whatever colour it wears; on:true gives a group its whole preview back",
            "GET /review": "the document against the composition rules: overlapping or unnamed groups, groups doing two jobs, bare boundary crossings, ungrouped objects",
            "POST /report": "{author, expected, got, notes?} - leave a note where a verb fought you: what you expected against what happened. Refused requests are logged by themselves; this is for the rest. Local file, never sent anywhere",
            "GET /friction?tail=50": "the friction log: refused requests and reports, newest last, with the file's path",
            "POST /feedback": "{author, expected, got, to?} - assembles the whole complaint into one readable file (session, review, recent friction) and answers with its path and a mailto link. Ask the human before calling it, and let them send it: nothing is sent from here",
            "POST /group": "{author, name, ids?:[guid], colour?:[r,g,b], inlets?:[name|{name,type}], outlets?:[...]} - a named group, declared signature first if you like: inlets and outlets are created as named floating parameters and answered as a name-to-id map, so the body can be wired onto them afterwards",
            "POST /ungroup": "{author, id} - dissolve a group, keeping its members",
            "GET /components?q=text": "search the installed component catalogue by name/description; top matches carry their inputs and outputs",
            "GET /canvas-image?width=1200&fit=true": "the Grasshopper canvas itself as PNG (base64), fitted to the whole document for the capture and the view put back after - for judging whether a layout reads",
            "GET /screenshot?width=640&zoomExtents=true": "the active Rhino viewport as PNG (base64) - low-res by default; framed on the geometry for the capture and the camera put back where the human left it (zoomExtents=false skips the framing)",
            "POST /escape": "{author, times?} - post Escape to Rhino, cancelling whatever it is waiting for. For the case /dismiss cannot answer: a command waiting on a pick is not a dialog, so nothing is disabled and there is no window to click, yet the UI thread is held and every verb reports 'busy' as though waiting would help. 'times' cancels that many levels; one by default",
            "GET /camera": "where the active viewport is looking: projection, camera location, target, up, 35mm lens length and the viewport's pixel size",
            "POST /camera": "{author, location?:[x,y,z], target?:[x,y,z], up?:[x,y,z], lens?, projection?:'perspective'|'parallel'} - aim the active viewport. Only what you pass changes. This is how to frame a particular view: the Zoom command is interactive and a scripted one waits for a pick that never comes, which hangs the UI thread and takes every other verb down with it",
            "GET /peek?id=guid&side=input|output&param=nameOrIndex": "the full data on one parameter, branch by branch with tree paths. Give a group's id instead and it answers that group's signature as it stands: every inlet and outlet with its type, branch and item counts, and a few values off each outlet",
            "GET /rhino": "the Rhino document: name, layers, object count",
            "GET /plugins": "what is loaded: Grasshopper libraries and loaded Rhino plug-ins, each with version and the file it came from. For when the suspect named in the console is a plug-in rather than a component",
            "POST /place": "{author, group?, objects:[{id?, name|guid, nickname?, pivot?, slider?, text?, value?, inputs?:[{param?, sources:[{id, output?}]}]}]} - a whole recipe in one call; local ids wire to each other and to existing canvas guids, 'group' puts everything placed into that group; answers the id map",
            "POST /save": "{author, path?} - save the document (autosave also runs once before an agent's first edit)",
            "POST /zoom": "{author, ids:[guid]} - focus the canvas view on those objects",
            "POST /rhino": "{author, script} - run a Rhino command script (layers, blocks, groups - the whole command language)",
            "GET /scripts": "the script components on the canvas, with their generation",
            "GET /script?id=guid": "one script component's source",
            "POST /script": "{author, id, source} - new source in, one solve, the component's errors and warnings back"
          },
          "discovery": "%TEMP%/phenome-link-<rhino pid>.port holds this port; no file, no session"
        }
        """;

    /// <summary>Binds an ephemeral loopback port and starts answering.</summary>
    internal static void Start()
    {
        // Before the listener, so the first request can already be told what Rhino is doing and what it
        // has been saying.
        Pulse.Start();
        CommandLine.Start();

        // The border that says an agent is driving. Started here rather than on the first request, so the
        // first request is already inside it.
        Attention.Start();

        listener = Listen();

        Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();

                    Served++;
                    LastRequest = DateTime.Now;

                    Answer(context);
                }
                catch (Exception) when (!listener.IsListening)
                {
                    // Shut down mid-await; not an incident.
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

        // Read once, up front: the body stream is single-pass, and a refusal cannot say what was asked
        // for unless the asking was kept.
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
                ("POST", "/report") => Process.Reported(Read(payload)),
                ("POST", "/feedback") => Process.Feedback(Read(payload)),
                ("GET", "/friction") => Friction.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int tail) ? Math.Clamp(tail, 1, 500) : 50),
                _ => throw new KeyNotFoundException($"There is no {method} {path}. GET / describes what there is."),
            };

            Echo(method, path, ok: true, said: null, clock);
            Respond(context.Response, 200, body);
        }
        catch (KeyNotFoundException missing)
        {
            Friction.Refused($"{method} {path}", payload, missing.Message);
            Echo(method, path, ok: false, said: missing.Message, clock);
            Respond(context.Response, 404, $"{{\"ok\":false,\"error\":{Json.Quote(missing.Message)}}}");
        }
        catch (Exception failure)
        {
            Friction.Refused($"{method} {path}", payload, failure.Message);
            Echo(method, path, ok: false, said: failure.Message, clock);
            Respond(context.Response, 500, $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}");
        }
    }

    /// <summary>
    /// One line per request in Rhino's own command line: the time, the verb, and whether it worked.
    /// </summary>
    /// <remarks>
    /// The journal and the VS Code channel are the full account; this is for the person sitting in front of
    /// Rhino watching an agent work, who wants to know it is doing something and where it stopped - without
    /// looking anywhere else. Queued onto the UI thread, since requests are answered on a worker.
    /// </remarks>
    private static void Echo(string method, string path, bool ok, string? said, System.Diagnostics.Stopwatch? clock = null)
    {
        // The heartbeat and the discovery probe are not news: a client polls the journal every couple of
        // seconds, and echoing that buries the one line the watcher actually wanted under a hundred that
        // say nothing happened. Failures are still worth hearing about, whatever asked.
        if (ok && (path == "/events" || path.Length == 0))
        {
            return;
        }

        // Fixed columns, because this is read down rather than across: the eye finds the one slow call or
        // the one refusal by scanning a column, and ragged text hides both. The verb loses its slash and
        // its HTTP method - GET or POST is the transport's business, not the watcher's.
        //
        // Two things this format is careful about, learned from looking at a screenful of it:
        //
        // The amount and its unit are separate columns, so "182" and "1.4" line up on their digits and a
        // slow call is found by the width of the number rather than by reading. Right-aligning the whole
        // "182ms" / "1.4s" string instead floats the unit, and then nothing lines up with anything.
        //
        // Nothing says "ok". A column of fourteen identical words carries no information and is the widest
        // thing on the line; what the watcher is scanning for is the one line that is *not* ok, so only
        // that one is marked, and marked in the column the eye is already travelling down.
        //
        // The port is on every line even though it never changes within a Rhino, because the place it used
        // to be - the banner written once at load - has scrolled off the top by the fifteenth request, and
        // it is the one fact somebody reading this needs in order to hand this session to an agent or to
        // check which of two Rhinos they are looking at. A constant column costs almost nothing to scan
        // past, and it means any screenshot of the command line carries the port with it.
        //
        // Written with its colon. Five bare digits beside a clock read as a number of unclear purpose; a
        // leading colon says "port" to anybody who has ever seen a URL, and costs one character. Padded on
        // the right rather than the left, unlike every other number here: the port does not vary within a
        // session, so lining its digits up buys nothing, while keeping the colon in a fixed column means
        // every line begins the same shape.
        string verb = path.Length == 0 ? "/" : path.TrimStart('/');
        (string amount, string unit) = clock is null ? ("", "") : Duration(clock.ElapsedMilliseconds);

        string line = string.Format(
            "  {0}  {1,-6} {2,-13}{3,6} {4,-2}{5}",
            DateTime.Now.ToString("HH:mm:ss"),
            $":{Port}",
            verb.Length > 13 ? verb.Substring(0, 13) : verb,
            amount,
            unit,
            ok || string.IsNullOrEmpty(said) ? "" : "  !!  " + OneLine(said));

        // Trailing padding is only useful under something, and on a success line there is nothing after
        // the unit.
        line = line.TrimEnd();

        // Claimed before it is written: the capture cannot tell this plugin's echo from Rhino's own
        // output, and an agent reading /console should not be shown its own footsteps as news.
        CommandLine.Ours(line);

        try
        {
            Rhino.RhinoApp.InvokeOnUiThread(() => Rhino.RhinoApp.WriteLine(line));
        }
        catch (Exception)
        {
            // A log line is never worth an incident of its own.
        }
    }

    /// <summary>
    /// A duration in the unit that reads at a glance, rather than four digits of milliseconds - split into
    /// the amount and the unit, so the caller can column them separately and the digits line up.
    /// </summary>
    private static (string Amount, string Unit) Duration(long milliseconds) =>
        milliseconds < 1000
            ? ($"{milliseconds}", "ms")
            : milliseconds < 60_000
                ? ($"{milliseconds / 1000.0:0.0}", "s")
                : ($"{milliseconds / 60_000}m{milliseconds % 60_000 / 1000:00}", "s");

    /// <summary>
    /// A message on one line and no longer than the command line can show. A refusal that wraps over four
    /// lines pushes everything before it off the top, which is the opposite of what the echo is for.
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

        // Open to any origin for the same reason the payload server is: the clients are local windows -
        // a webview, a browser tab - and the listener never leaves the loopback.
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.Close();
    }

    // ---- The verbs -------------------------------------------------------------------------------------

    /// <summary>
    /// Binds a listener on an ephemeral loopback port and publishes the port it settled on.
    /// </summary>
    /// <remarks>
    /// The retrying is in <see cref="Loopback.Listen"/>, shared with the Rhino half. What stays here is the
    /// one thing that is this class's business: <see cref="Port"/> is assigned from the out parameter, so it
    /// only ever holds a port a listener is actually running on.
    /// </remarks>
    private static HttpListener Listen()
    {
        HttpListener listening = Loopback.Listen(out int port);

        Port = port;

        return listening;
    }
}
