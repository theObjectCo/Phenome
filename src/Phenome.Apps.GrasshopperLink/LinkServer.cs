using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

namespace Phenome.Apps.GrasshopperLink;

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

    private const string Description = """
        {
          "phenome": "grasshopper-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /canvas": "the whole document: every object, wires, values, selection, enabled, preview, mapping, solver state",
            "GET /canvas?as=mermaid": "the same document as a mermaid flowchart - groups as subgraphs, red components marked - with a map of short node ids to real guids. The shape of a definition at a fiftieth of the size; it carries no data, so branch and item counts still come from peek",
            "GET /events?since=N": "the journal after entry N; response carries 'latest' to ask from next time; a gap below your cursor means entries were dropped - re-read /canvas",
            "POST /dismiss": "{author, button?, expect?} - answer the dialog Rhino is waiting on: press a button by name, or close it when no name is given. 'expect' names the dialog you meant to answer and refuses if another one is up by then. /pulse lists the buttons",
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
            "GET /review": "the document against the composition rules: overlapping or unnamed groups, groups doing two jobs, bare boundary crossings, ungrouped objects",
            "POST /report": "{author, expected, got, notes?} - leave a note where a verb fought you: what you expected against what happened. Refused requests are logged by themselves; this is for the rest. Local file, never sent anywhere",
            "GET /friction?tail=50": "the friction log: refused requests and reports, newest last, with the file's path",
            "POST /feedback": "{author, expected, got, to?} - assembles the whole complaint into one readable file (session, review, recent friction) and answers with its path and a mailto link. Ask the human before calling it, and let them send it: nothing is sent from here",
            "POST /group": "{author, name, ids?:[guid], colour?:[r,g,b], inlets?:[name|{name,type}], outlets?:[...]} - a named group, declared signature first if you like: inlets and outlets are created as named floating parameters and answered as a name-to-id map, so the body can be wired onto them afterwards",
            "POST /ungroup": "{author, id} - dissolve a group, keeping its members",
            "GET /components?q=text": "search the installed component catalogue by name/description; top matches carry their inputs and outputs",
            "GET /canvas-image?width=1200&fit=true": "the Grasshopper canvas itself as PNG (base64), fitted to the whole document for the capture and the view put back after - for judging whether a layout reads",
            "GET /screenshot?width=640&zoomExtents=true": "the active Rhino viewport as PNG (base64) - low-res by default; framed on the geometry for the capture and the camera put back where the human left it (zoomExtents=false skips the framing)",
            "GET /peek?id=guid&side=input|output&param=nameOrIndex": "the full data on one parameter, branch by branch with tree paths. Give a group's id instead and it answers that group's signature as it stands: every inlet and outlet with its type, branch and item counts, and a few values off each outlet",
            "GET /rhino": "the Rhino document: name, layers, object count",
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
        Port = FreePort();

        // Before the listener, so the first request can already be told what Rhino is doing and what it
        // has been saying.
        Pulse.Start();
        CommandLine.Start();

        // The border that says an agent is driving. Started here rather than on the first request, so the
        // first request is already inside it.
        Attention.Start();

        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

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
                ("POST", "/dismiss") => Dismissed(Read(payload)),
                ("GET", "/console") => CommandLine.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int back) ? Math.Clamp(back, 1, 500) : 50),
                ("POST", "/say") => Say(Read(payload)),
                ("POST", "/solver") => Solver(Read(payload)),
                ("POST", "/bake") => Bake(Read(payload)),
                ("POST", "/param") => Mapping(Read(payload)),
                ("POST", "/new") => NewDocument(Read(payload)),
                ("POST", "/open") => Open(Read(payload)),
                ("POST", "/add") => Add(Read(payload)),
                ("POST", "/wire") => Wire(Read(payload)),
                ("POST", "/set") => SetValue(Read(payload)),
                ("POST", "/select") => Select(Read(payload)),
                ("POST", "/delete") => Delete(Read(payload)),
                ("GET", "/wires") => OnUi(Wires),
                ("GET", "/describe") => OnUi(() => Describe(
                    Guid.Parse(context.Request.QueryString["id"]
                        ?? throw new ArgumentException("describe needs ?id=guid.")))),
                ("POST", "/undo") => Undo(Read(payload), forward: false),
                ("POST", "/redo") => Undo(Read(payload), forward: true),
                ("POST", "/arrange") => DoArrange(Read(payload)),
                ("POST", "/signature") => DoSignature(Read(payload)),
                ("GET", "/review") => OnUi(() => Review.Whole(ActiveDocument())),
                ("POST", "/group") => Group(Read(payload)),
                ("POST", "/ungroup") => Ungroup(Read(payload)),
                ("GET", "/components") => OnUi(() => Catalogue.Search(
                    context.Request.QueryString["q"]
                        ?? throw new ArgumentException("components needs ?q=text."))),
                ("GET", "/screenshot") => Screenshot(context.Request),
                ("GET", "/canvas-image") => CanvasImage(context.Request),
                ("GET", "/peek") => Peek(context.Request),
                ("GET", "/rhino") => OnUi(RhinoSummary),
                ("POST", "/place") => Place(Read(payload)),
                ("POST", "/save") => Save(Read(payload)),
                ("POST", "/zoom") => Zoom(Read(payload)),
                ("POST", "/rhino") => RunScript(Read(payload)),
                ("GET", "/scripts") => OnUi(() => Scripts.List(ActiveDocument())),
                ("GET", "/script") => OnUi(() => Scripts.Read(
                    ActiveDocument(),
                    Guid.Parse(context.Request.QueryString["id"]
                        ?? throw new ArgumentException("script needs ?id=guid.")))),
                ("POST", "/script") => WriteScript(Read(payload)),
                ("POST", "/report") => Reported(Read(payload)),
                ("POST", "/feedback") => Feedback(Read(payload)),
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
        string verb = path.Length == 0 ? "/" : path.TrimStart('/');
        string elapsed = clock is null ? "" : Duration(clock.ElapsedMilliseconds);

        string line = string.Format(
            "  {0}  {1,-14} {2,-4} {3,7}{4}",
            DateTime.Now.ToString("HH:mm:ss"),
            verb.Length > 14 ? verb.Substring(0, 14) : verb,
            ok ? "ok" : "FAIL",
            elapsed,
            ok || string.IsNullOrEmpty(said) ? "" : "   " + OneLine(said));

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

    /// <summary>A duration in the unit that reads at a glance, rather than four digits of milliseconds.</summary>
    private static string Duration(long milliseconds) =>
        milliseconds < 1000
            ? $"{milliseconds}ms"
            : milliseconds < 60_000
                ? $"{milliseconds / 1000.0:0.0}s"
                : $"{milliseconds / 60_000}m{milliseconds % 60_000 / 1000:00}s";

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

    private static string Say(JsonDocument request)
    {
        string author = Author(request);
        string text = Field(request, "text") ?? throw new ArgumentException("say needs 'text'.");

        Journal.AppendMessage(author, text, Field(request, "to"));

        return "{\"ok\":true}";
    }

    private static string Solver(JsonDocument request)
    {
        string author = Author(request);
        bool enabled = request.RootElement.TryGetProperty("enabled", out JsonElement flag) && AsBool(flag);

        OnUi(() =>
        {
            GH_Document.EnableSolutions = enabled;

            if (enabled)
            {
                ActiveDocument()?.NewSolution(false);
            }

            return true;
        });

        Journal.Append(author, "solver", $",\"enabled\":{(enabled ? "true" : "false")}");

        return "{\"ok\":true}";
    }

    private static string Bake(JsonDocument request)
    {
        string author = Author(request);

        if (!request.RootElement.TryGetProperty("ids", out JsonElement ids))
        {
            throw new ArgumentException("bake needs 'ids' - which objects to bake.");
        }

        List<Guid> asked = [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))];

        int baked = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to bake from.");

            Rhino.RhinoDoc rhino = Rhino.RhinoDoc.ActiveDoc
                ?? throw new InvalidOperationException("There is no Rhino document to bake into.");

            List<Guid> born = [];

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is IGH_BakeAwareObject { IsBakeCapable: true } thing)
                {
                    thing.BakeGeometry(rhino, born);
                }
            }

            rhino.Views.Redraw();

            return born.Count;
        });

        Journal.Append(author, "bake", $",\"objects\":{Json.Number(asked.Count)},\"baked\":{Json.Number(baked)}");

        return $"{{\"ok\":true,\"baked\":{Json.Number(baked)}}}";
    }

    private static string Mapping(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("param needs 'id'."));

        bool changed = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            IGH_Param parameter = Locate(thing, request);

            EnsureAutosave(document);
            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: data mapping", thing);

            if (Field(request, "mapping") is { } mapping)
            {
                parameter.DataMapping = mapping switch
                {
                    "flatten" => GH_DataMapping.Flatten,
                    "graft" => GH_DataMapping.Graft,
                    "none" => GH_DataMapping.None,
                    _ => throw new ArgumentException($"mapping is 'none', 'flatten' or 'graft', not '{mapping}'."),
                };
            }

            if (request.RootElement.TryGetProperty("simplify", out JsonElement simplify))
            {
                parameter.Simplify = AsBool(simplify);
            }

            if (request.RootElement.TryGetProperty("reverse", out JsonElement reverse))
            {
                parameter.Reverse = AsBool(reverse);
            }

            parameter.ExpireSolution(false);
            document.NewSolution(false);

            return true;
        });

        Journal.Append(author, "param", $",\"id\":{Json.Quote(id.ToString())}");

        return $"{{\"ok\":{(changed ? "true" : "false")}}}";
    }

    /// <summary>The parameter a request points at: the object itself, or one of a component's by side and name/index.</summary>
    private static IGH_Param Locate(IGH_DocumentObject thing, JsonDocument request) =>
        LocateBy(thing, Field(request, "side"), Field(request, "param"));

    /// <summary>The same aim without a request: side is input unless said, no name means the only one.</summary>
    private static IGH_Param LocateBy(IGH_DocumentObject thing, string? whichSide, string? param)
    {
        if (thing is IGH_Param loose)
        {
            return loose;
        }

        if (thing is not IGH_Component component)
        {
            throw new ArgumentException($"{thing.Name} has no parameters.");
        }

        List<IGH_Param> side = whichSide == "output"
            ? component.Params.Output
            : component.Params.Input;

        if (param is null)
        {
            return side.Count == 1
                ? side[0]
                : throw new ArgumentException($"{component.Name} has {side.Count} on that side - say which with 'param'.");
        }

        if (int.TryParse(param, out int index))
        {
            if (index < 0 || index >= side.Count)
            {
                throw new ArgumentException($"{component.Name} has {side.Count} on that side; {index} is not one of them.");
            }

            return side[index];
        }

        return side.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, param, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.NickName, param, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"{component.Name} has no parameter '{param}'.");
    }

    private static string NewDocument(JsonDocument request)
    {
        string author = Author(request);

        OnUi(() =>
        {
            GH_Document document = new();

            global::Grasshopper.Instances.DocumentServer.AddDocument(document);

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                canvas.Document = document;
            }

            return true;
        });

        Journal.Append(author, "documentNew");

        return "{\"ok\":true}";
    }

    private static string Open(JsonDocument request)
    {
        string author = Author(request);
        string path = Field(request, "path") ?? throw new ArgumentException("open needs 'path'.");

        if (!File.Exists(path))
        {
            throw new KeyNotFoundException($"There is no file at {path}.");
        }

        OnUi(() =>
        {
            if (path.EndsWith(".3dm", StringComparison.OrdinalIgnoreCase))
            {
                Rhino.RhinoDoc.Open(path, out bool _);
                return true;
            }

            GH_DocumentIO reader = new();

            if (!reader.Open(path))
            {
                throw new InvalidOperationException($"Grasshopper could not open {path}.");
            }

            GH_Document document = reader.Document;

            global::Grasshopper.Instances.DocumentServer.AddDocument(document);

            if (global::Grasshopper.Instances.ActiveCanvas is { } canvas)
            {
                canvas.Document = document;
            }

            return true;
        });

        Journal.Append(author, "documentOpenAsked", $",\"path\":{Json.Quote(path)}");

        return "{\"ok\":true}";
    }

    private static string Add(JsonDocument request)
    {
        string author = Author(request);
        string? name = Field(request, "name");
        string? guid = Field(request, "guid");

        if (name is null && guid is null)
        {
            throw new ArgumentException("add needs 'name' or 'guid' - which component to put down.");
        }

        Guid id = OnUi(() =>
        {
            IGH_ObjectProxy proxy = guid is not null
                ? global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(Guid.Parse(guid))
                    ?? throw new KeyNotFoundException($"No component with guid {guid} is installed.")
                : global::Grasshopper.Instances.ComponentServer.ObjectProxies
                    .FirstOrDefault(candidate =>
                        !candidate.Obsolete
                        && string.Equals(candidate.Desc.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"No component is called '{name}'.");

            IGH_DocumentObject thing = proxy.CreateInstance()
                ?? throw new InvalidOperationException($"{proxy.Desc.Name} would not instantiate.");

            if (Field(request, "nickname") is { } nickname)
            {
                thing.NickName = nickname;
            }

            thing.CreateAttributes();

            if (request.RootElement.TryGetProperty("pivot", out JsonElement pivot))
            {
                thing.Attributes.Pivot = new System.Drawing.PointF(
                    (float)pivot[0].GetDouble(),
                    (float)pivot[1].GetDouble());
            }

            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to add to.");

            EnsureAutosave(document);

            document.AddObject(thing, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: add", thing);
            document.NewSolution(false);

            return thing.InstanceGuid;
        });

        Journal.Append(author, "add", $",\"id\":{Json.Quote(id.ToString())},\"name\":{Json.Quote(name ?? guid!)}");

        return $"{{\"ok\":true,\"id\":{Json.Quote(id.ToString())}}}";
    }

    /// <summary>
    /// One wire, or all of them: a 'wires' array is applied in one pass with a single solution at the end.
    /// </summary>
    /// <remarks>
    /// The batch is the point rather than a convenience. A definition is mostly wires, and one call per
    /// wire means one round trip, one permission thought and one solution each - the agent spends its
    /// afternoon on plumbing, and the human watches the canvas flicker forty times.
    /// </remarks>
    private static string Wire(JsonDocument request)
    {
        string author = Author(request);

        List<JsonElement> asked = request.RootElement.TryGetProperty("wires", out JsonElement many)
            ? [.. many.EnumerateArray()]
            : [request.RootElement];

        int made = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            int count = 0;

            foreach (JsonElement wire in asked)
            {
                IGH_Param source = End(document, wire, "from", outputSide: true);
                IGH_Param target = End(document, wire, "to", outputSide: false);

                document.UndoUtil.RecordWireEvent("Phenome Link: wire", target);

                if (wire.TryGetProperty("disconnect", out JsonElement take) && AsBool(take))
                {
                    target.RemoveSource(source);
                }
                else
                {
                    target.AddSource(source);
                }

                target.ExpireSolution(false);
                count++;
            }

            document.NewSolution(false);

            return count;
        });

        Journal.Append(author, "wire", $",\"wires\":{Json.Number(made)}");

        return $"{{\"ok\":true,\"wires\":{Json.Number(made)}}}";
    }

    /// <summary>One value, or all of them: a 'values' array is applied in one pass, one solution at the end.</summary>
    private static string SetValue(JsonDocument request)
    {
        string author = Author(request);

        List<JsonElement> asked = request.RootElement.TryGetProperty("values", out JsonElement many)
            ? [.. many.EnumerateArray()]
            : [request.RootElement];

        int made = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            int count = 0;

            foreach (JsonElement one in asked)
            {
                Apply(document, one);
                count++;
            }

            document.NewSolution(false);

            return count;
        });

        Journal.Append(author, "set", $",\"values\":{Json.Number(made)}");

        return $"{{\"ok\":true,\"values\":{Json.Number(made)}}}";
    }

    private static void Apply(GH_Document document, JsonElement request)
    {
        Guid id = Guid.Parse(Text(request, "id") ?? throw new ArgumentException("set needs 'id'."));

        if (!request.TryGetProperty("value", out JsonElement value))
        {
            throw new ArgumentException("set needs 'value'.");
        }

        {
            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            document.UndoUtil.RecordGenericObjectEvent("Phenome Link: set", thing);

            // With 'param', the value goes into a component's own input - the way a human types a constant
            // straight into a socket instead of standing up a parameter and a wire for the number two.
            if (Text(request, "param") is { } which && thing is IGH_Component)
            {
                IGH_Param socket = LocateBy(thing, "input", which);

                Store(socket, value);
                socket.ExpireSolution(false);

                return;
            }

            switch (thing)
            {
                case Grasshopper.Kernel.Special.GH_NumberSlider slider:
                    // Bounds before value, so the value is clamped against where the slider is going, not
                    // where it was. A string value is GH's own init notation - "0<50<100" says all three.
                    if (request.TryGetProperty("minimum", out JsonElement minimum))
                    {
                        slider.Slider.Minimum = (decimal)AsDouble(minimum);
                    }

                    if (request.TryGetProperty("maximum", out JsonElement maximum))
                    {
                        slider.Slider.Maximum = (decimal)AsDouble(maximum);
                    }

                    if (request.TryGetProperty("decimals", out JsonElement decimals))
                    {
                        slider.Slider.DecimalPlaces = (int)AsDouble(decimals);
                        slider.Slider.Type = (int)AsDouble(decimals) == 0
                            ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer
                            : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
                    }

                    // Only a domain expression goes through the init code; "42" spelt as a string is a
                    // number a client was too casual about, not a domain.
                    if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains('<'))
                    {
                        slider.SetInitCode(value.GetString());
                    }
                    else
                    {
                        slider.SetSliderValue((decimal)AsDouble(value));
                    }

                    break;

                case Grasshopper.Kernel.Special.GH_Panel panel:
                    panel.UserText = value.ValueKind == JsonValueKind.String
                        ? value.GetString()!
                        : value.ToString();
                    break;

                case Grasshopper.Kernel.Special.GH_BooleanToggle toggle:
                    toggle.Value = AsBool(value);
                    break;

                // A swatch keeps its colour in a property of its own rather than as parameter data, so the
                // generic path refused it - and a definition whose whole point is four coloured shelf edges
                // could not be coloured.
                case Grasshopper.Kernel.Special.GH_ColourSwatch swatch:
                    swatch.SwatchColour = AsColour(value);
                    break;

                case IGH_Param parameter:
                    Store(parameter, value);
                    break;

                default:
                    throw new ArgumentException($"{thing.Name} holds no value to set.");
            }

            thing.ExpireSolution(false);
        }
    }

    private static string Select(JsonDocument request)
    {
        string author = Author(request);
        bool add = request.RootElement.TryGetProperty("add", out JsonElement extend) && AsBool(extend);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("select needs 'ids'.");

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            if (!add)
            {
                foreach (IGH_DocumentObject thing in document.Objects)
                {
                    if (thing.Attributes is { } attributes)
                    {
                        attributes.Selected = false;
                    }
                }
            }

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { Attributes: { } attributes })
                {
                    attributes.Selected = true;
                }
            }

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return true;
        });

        Journal.Append(author, "select", $",\"count\":{Json.Number(asked.Count)}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// Removes objects - and refuses, unless forced, when that would cut a wire to something staying.
    /// </summary>
    /// <remarks>
    /// A bulk delete of "unused" objects severed a live definition in the field: the objects looked idle
    /// but fed things that stayed, and the damage arrived all at once with nothing to point at. So the
    /// wires that would be cut are counted first and named back to the caller; force says you meant it.
    /// </remarks>
    private static string Delete(JsonDocument request)
    {
        string author = Author(request);
        bool force = request.RootElement.TryGetProperty("force", out JsonElement mean) && AsBool(mean);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("delete needs 'ids'.");

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            HashSet<Guid> going = [.. asked];
            List<string> severed = [];

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is not { } leaving)
                {
                    continue;
                }

                foreach (IGH_Param output in OutputsOf(leaving))
                {
                    foreach (IGH_Param reader in output.Recipients)
                    {
                        IGH_DocumentObject owner = reader.Attributes?.GetTopLevel?.DocObject ?? reader;

                        if (!going.Contains(owner.InstanceGuid))
                        {
                            severed.Add($"{Named(leaving)} → {Named(owner)}.{reader.Name}");
                        }
                    }
                }
            }

            if (severed.Count > 0 && !force)
            {
                StringBuilder cuts = new();

                foreach (string cut in severed.Take(20))
                {
                    cuts.Append(cuts.Length > 0 ? "," : "").Append(Json.Quote(cut));
                }

                return $"{{\"ok\":false,\"removed\":0,\"wouldSever\":{Json.Number(severed.Count)},"
                    + $"\"wires\":[{cuts}],\"error\":\"Deleting these would cut {severed.Count} wire(s) to "
                    + "objects that stay. Check the list, then pass force:true if you mean it.\"}";
            }

            EnsureAutosave(document);

            int gone = 0;

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { } thing)
                {
                    document.UndoUtil.RecordRemoveObjectEvent("Phenome Link: delete", thing);
                    document.RemoveObject(thing, update: false);
                    gone++;
                }
            }

            document.NewSolution(false);

            return $"{{\"ok\":true,\"removed\":{Json.Number(gone)},\"severed\":{Json.Number(severed.Count)}}}";
        });

        Journal.Append(author, "delete", $",\"asked\":{Json.Number(asked.Count)}");

        return answer;
    }

    /// <summary>
    /// One object's parameters by name, so nobody has to search the catalogue for something already placed.
    /// </summary>
    private static string Describe(Guid id)
    {
        GH_Document document = ActiveDocument()
            ?? throw new InvalidOperationException("There is no document.");

        IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
            ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

        StringBuilder json = new("{\"id\":");

        json.Append(Json.Quote(id.ToString()));
        json.Append(",\"name\":").Append(Json.Quote(thing.Name));
        json.Append(",\"nickname\":").Append(Json.Quote(thing.NickName));

        Ports("inputs", Arrange.InputsOf(thing), json);
        Ports("outputs", OutputsOf(thing), json);

        return json.Append('}').ToString();

        static void Ports(string side, IEnumerable<IGH_Param> these, StringBuilder into)
        {
            into.Append($",\"{side}\":[");

            bool first = true;
            int index = 0;

            foreach (IGH_Param param in these)
            {
                if (!first)
                {
                    into.Append(',');
                }

                first = false;

                into.Append("{\"index\":").Append(Json.Number(index++));
                into.Append(",\"name\":").Append(Json.Quote(param.Name));
                into.Append(",\"nickname\":").Append(Json.Quote(param.NickName));
                into.Append(",\"type\":").Append(Json.Quote(param.TypeName));
                into.Append(",\"access\":").Append(Json.Quote(param.Access.ToString().ToLowerInvariant()));
                into.Append(",\"wired\":").Append(Json.Number(param.SourceCount));
                into.Append(",\"holds\":").Append(Json.Number(param.VolatileDataCount));

                if (param.Optional)
                {
                    into.Append(",\"optional\":true");
                }

                into.Append('}');
            }

            into.Append(']');
        }
    }

    /// <summary>Every wire in the document - the whole picture, which no per-input peek adds up to.</summary>
    private static string Wires()
    {
        GH_Document document = ActiveDocument()
            ?? throw new InvalidOperationException("There is no document.");

        StringBuilder json = new("{\"wires\":[");
        bool first = true;

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            foreach (IGH_Param input in Arrange.InputsOf(thing))
            {
                foreach (IGH_Param source in input.Sources)
                {
                    IGH_DocumentObject from = source.Attributes?.GetTopLevel?.DocObject ?? source;

                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;

                    json.Append("{\"from\":{\"id\":").Append(Json.Quote(from.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(Named(from)));

                    if (from is IGH_Component component)
                    {
                        json.Append(",\"param\":").Append(Json.Quote(source.Name));
                    }

                    json.Append("},\"to\":{\"id\":").Append(Json.Quote(thing.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(Named(thing)));
                    json.Append(",\"param\":").Append(Json.Quote(input.Name)).Append("}}");
                }
            }
        }

        return json.Append("]}").ToString();
    }

    /// <summary>One step back, or forward - Grasshopper's own undo stack, which every verb records into.</summary>
    private static string Undo(JsonDocument request, bool forward)
    {
        string author = Author(request);

        // Answered with what the document looks like afterwards, because a step's name alone reads as
        // nonsense: undoing a delete puts objects back, so a caller watching only the count sees it grow
        // and concludes undo is broken. It was not; it was working.
        (string what, int before, int after) = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            int had = document.ObjectCount;
            string name = forward ? document.UndoServer.FirstRedoName : document.UndoServer.FirstUndoName;
            int waiting = forward ? document.UndoServer.RedoCount : document.UndoServer.UndoCount;

            if (waiting == 0)
            {
                throw new InvalidOperationException(forward
                    ? "There is nothing to redo."
                    : "There is nothing to undo.");
            }

            if (forward)
            {
                document.UndoServer.PerformRedo();
            }
            else
            {
                document.UndoServer.PerformUndo();
            }

            document.NewSolution(false);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return (name, had, document.ObjectCount);
        });

        Journal.Append(author, forward ? "redo" : "undo", $",\"step\":{Json.Quote(what)}");

        return $"{{\"ok\":true,\"step\":{Json.Quote(what)},\"objectsBefore\":{Json.Number(before)},"
            + $"\"objectsAfter\":{Json.Number(after)},\"remaining\":{Json.Number(Steps(forward))}}}";
    }

    /// <summary>How many steps are left on that side of the stack.</summary>
    private static int Steps(bool forward) => OnUi(() =>
    {
        GH_Document? document = ActiveDocument();

        return document is null
            ? 0
            : forward ? document.UndoServer.RedoCount : document.UndoServer.UndoCount;
    });

    private static string Named(IGH_DocumentObject thing) =>
        string.IsNullOrWhiteSpace(thing.NickName) ? thing.Name : thing.NickName;

    private static IEnumerable<IGH_Param> OutputsOf(IGH_DocumentObject thing) => thing switch
    {
        IGH_Component component => component.Params.Output,
        IGH_Param parameter => [parameter],
        _ => [],
    };

    private static string WriteScript(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("script needs 'id'."));
        string source = Field(request, "source") ?? throw new ArgumentException("script needs 'source'.");

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            return Scripts.Write(document, id, source);
        });

        Journal.Append(author, "script", $",\"id\":{Json.Quote(id.ToString())}");

        return answer;
    }

    private static string DoArrange(JsonDocument request)
    {
        string author = Author(request);

        int moved = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to arrange.");

            EnsureAutosave(document);

            int count = Arrange.Whole(document);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return count;
        });

        Journal.Append(author, "arrange", $",\"moved\":{Json.Number(moved)}");

        return $"{{\"ok\":true,\"moved\":{Json.Number(moved)}}}";
    }

    /// <summary>
    /// Answers the dialog Rhino is waiting on. Journalled like any other hand on the machine.
    /// </summary>
    private static string Dismissed(JsonDocument request)
    {
        string author = Author(request);
        string? button = Field(request, "button");
        string? expect = Field(request, "expect");

        string answer = Pulse.Dismiss(button, expect);

        Journal.Append(author, "dismiss", $",\"button\":{Json.Quote(button ?? "close")}");

        return answer;
    }

    private static string Reported(JsonDocument request)
    {
        string author = Author(request);
        string expected = Field(request, "expected") ?? throw new ArgumentException("report needs 'expected'.");
        string got = Field(request, "got") ?? throw new ArgumentException("report needs 'got'.");

        Friction.Reported(author, expected, got, Field(request, "notes"));

        // Into the journal as well, so the human watching sees the complaint as it is made rather than
        // discovering it in a file later.
        Journal.Append(author, "report", $",\"expected\":{Json.Quote(expected)},\"got\":{Json.Quote(got)}");

        return $"{{\"ok\":true,\"log\":{Json.Quote(Friction.Path)}}}";
    }

    private static string Feedback(JsonDocument request)
    {
        string author = Author(request);
        string expected = Field(request, "expected") ?? throw new ArgumentException("feedback needs 'expected'.");
        string got = Field(request, "got") ?? throw new ArgumentException("feedback needs 'got'.");

        (string session, string findings) = OnUi(() =>
        {
            GH_Document? document = ActiveDocument();

            string where = document is null
                ? "No Grasshopper document open."
                : $"Document '{document.DisplayName ?? "unsaved"}', {document.ObjectCount} objects, "
                    + $"solver {(GH_Document.EnableSolutions ? "on" : "locked")}.";

            return (where, Review.Whole(document));
        });

        (string path, string subject, _, string mailto) =
            Friction.Draft(expected, got, session, findings, Field(request, "to"));

        Journal.Append(author, "feedback", $",\"path\":{Json.Quote(path)}");

        return $"{{\"ok\":true,\"path\":{Json.Quote(path)},\"subject\":{Json.Quote(subject)},"
            + $"\"mailto\":{Json.Quote(mailto)},\"sent\":false}}";
    }

    private static string DoSignature(JsonDocument request)
    {
        string author = Author(request);
        Guid? only = Field(request, "id") is { } id ? Guid.Parse(id) : null;

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            string made = Signature.Apply(document, only);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return made;
        });

        Journal.Append(author, "signature");

        return answer;
    }

    private static string Group(JsonDocument request)
    {
        string author = Author(request);
        string name = Field(request, "name") ?? throw new ArgumentException("group needs 'name'.");

        // No members is not an error: a group declared signature-first has none yet, which is the point.
        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : [];

        // Declared up front, before there is a body: this is what lets a definition be built the way code
        // is written - the signature first, the innards after. Answered as a name-to-id map, so the body
        // can wire straight onto them.
        List<(string Name, string? Type)> inlets = Names(request, "inlets");
        List<(string Name, string? Type)> outlets = Names(request, "outlets");
        Dictionary<string, Guid> made = [];

        Guid born = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            EnsureAutosave(document);

            // With an id, this is a rename and recolour of a group that exists - the alternative was an
            // ungroup-and-regroup dance that leaves duplicates behind if anything goes wrong halfway.
            if (Field(request, "id") is { } existing)
            {
                if (document.FindObject(Guid.Parse(existing), topLevelOnly: true)
                    is not Grasshopper.Kernel.Special.GH_Group already)
                {
                    throw new KeyNotFoundException($"No group {existing} on the canvas.");
                }

                document.UndoUtil.RecordGenericObjectEvent("Phenome Link: group", already);

                already.NickName = name;

                if (request.RootElement.TryGetProperty("colour", out JsonElement recolour))
                {
                    already.Colour = System.Drawing.Color.FromArgb(
                        64,
                        recolour[0].GetInt32(),
                        recolour[1].GetInt32(),
                        recolour[2].GetInt32());
                }

                foreach (Guid id in asked)
                {
                    already.AddObject(id);
                }

                already.ExpireCaches();
                global::Grasshopper.Instances.ActiveCanvas?.Refresh();

                return already.InstanceGuid;
            }

            // The ports go down first and the group is drawn around them: a group created empty and then
            // filled has to have its frame recomputed anyway, and an object added to a group that does not
            // yet know its own bounds is how frames end up in the wrong place.
            //
            // A lane per group, stacked down the Y axis and far enough apart to stay apart. arrange will
            // lay the whole thing out properly at the end, but a human watching an agent work needs to
            // read the canvas *while* it is being built - and everything landing in one pile is unreadable
            // exactly when intervening would help most.
            float x = 100;
            float y = 100 + (document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>().Count() * 260);

            foreach ((string side, List<(string Name, string? Type)> these, float offset) in
                new[] { ("inlet", inlets, 0f), ("outlet", outlets, 900f) })
            {
                float at = y;

                foreach ((string what, string? type) in these)
                {
                    IGH_Param port = PortFor(type);

                    port.NickName = what;
                    port.CreateAttributes();
                    port.Attributes.Pivot = new System.Drawing.PointF(x + offset, at);
                    at += 32;

                    document.AddObject(port, update: false);
                    document.UndoUtil.RecordAddObjectEvent($"Phenome Link: {side}", port);

                    made[what] = port.InstanceGuid;
                }
            }

            Grasshopper.Kernel.Special.GH_Group group = new()
            {
                NickName = name,
            };

            if (request.RootElement.TryGetProperty("colour", out JsonElement colour))
            {
                // A quarter opacity, the way the reference definitions paint them: the colour names the
                // role, the wires underneath stay readable.
                group.Colour = System.Drawing.Color.FromArgb(
                    64,
                    colour[0].GetInt32(),
                    colour[1].GetInt32(),
                    colour[2].GetInt32());
            }

            document.AddObject(group, update: false);
            document.UndoUtil.RecordAddObjectEvent("Phenome Link: group", group);

            foreach (Guid id in asked)
            {
                group.AddObject(id);
            }

            foreach (Guid port in made.Values)
            {
                group.AddObject(port);
            }

            group.ExpireCaches();

            // To the very back of the draw order: a group made around existing groups is the mother, and
            // the mother is painted behind her children or she hides them.
            document.ArrangeObject(group, GH_Arrange.MoveToBack);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return group.InstanceGuid;
        });

        Journal.Append(author, "group", $",\"id\":{Json.Quote(born.ToString())},\"name\":{Json.Quote(name)}");

        StringBuilder ports = new();

        foreach ((string what, Guid id) in made)
        {
            ports.Append(ports.Length > 0 ? "," : "").Append(Json.Quote(what)).Append(':').Append(Json.Quote(id.ToString()));
        }

        return $"{{\"ok\":true,\"id\":{Json.Quote(born.ToString())},\"ports\":{{{ports}}}}}";
    }

    /// <summary>
    /// The ports a request lists for one side of a group's signature: a bare name, or a name with a type.
    /// </summary>
    private static List<(string Name, string? Type)> Names(JsonDocument request, string side) =>
        request.RootElement.TryGetProperty(side, out JsonElement these)
            ? [.. these.EnumerateArray().Select(one => one.ValueKind == JsonValueKind.String
                ? (one.GetString()!, (string?)null)
                : (one.GetProperty("name").GetString()!, Text(one, "type")))]
            : [];

    /// <summary>
    /// A parameter to stand at a group's edge. Typed when the caller says so, generic otherwise - and a
    /// generic port carries anything, which is the right default for a signature still being sketched.
    /// </summary>
    private static IGH_Param PortFor(string? type) => (type ?? "").ToLowerInvariant() switch
    {
        "number" or "double" => new Grasshopper.Kernel.Parameters.Param_Number(),
        "integer" or "int" => new Grasshopper.Kernel.Parameters.Param_Integer(),
        "text" or "string" => new Grasshopper.Kernel.Parameters.Param_String(),
        "boolean" or "bool" => new Grasshopper.Kernel.Parameters.Param_Boolean(),
        "point" => new Grasshopper.Kernel.Parameters.Param_Point(),
        "vector" => new Grasshopper.Kernel.Parameters.Param_Vector(),
        "plane" => new Grasshopper.Kernel.Parameters.Param_Plane(),
        "line" => new Grasshopper.Kernel.Parameters.Param_Line(),
        "curve" => new Grasshopper.Kernel.Parameters.Param_Curve(),
        "surface" => new Grasshopper.Kernel.Parameters.Param_Surface(),
        "brep" => new Grasshopper.Kernel.Parameters.Param_Brep(),
        "mesh" => new Grasshopper.Kernel.Parameters.Param_Mesh(),
        "geometry" => new Grasshopper.Kernel.Parameters.Param_Geometry(),
        "interval" or "domain" => new Grasshopper.Kernel.Parameters.Param_Interval(),
        "colour" or "color" => new Grasshopper.Kernel.Parameters.Param_Colour(),
        "transform" => new Grasshopper.Kernel.Parameters.Param_Transform(),
        _ => new Grasshopper.Kernel.Parameters.Param_GenericObject(),
    };

    private static string Ungroup(JsonDocument request)
    {
        string author = Author(request);
        Guid id = Guid.Parse(Field(request, "id") ?? throw new ArgumentException("ungroup needs 'id'."));

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            if (document.FindObject(id, topLevelOnly: true) is not Grasshopper.Kernel.Special.GH_Group group)
            {
                throw new KeyNotFoundException($"No group {id} on the canvas.");
            }

            EnsureAutosave(document);
            document.UndoUtil.RecordRemoveObjectEvent("Phenome Link: ungroup", group);
            document.RemoveObject(group, update: false);
            global::Grasshopper.Instances.ActiveCanvas?.Refresh();

            return true;
        });

        Journal.Append(author, "ungroup", $",\"id\":{Json.Quote(id.ToString())}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// The Grasshopper canvas as a picture, so an author can see whether their layout reads.
    /// </summary>
    /// <remarks>
    /// Two agents in a row said the same thing: they could see the geometry but never the canvas, so
    /// "is this readable" had to be inferred from coordinates and a lint. Captured through the control's own
    /// DrawToBitmap rather than Grasshopper's export pipeline, which answers a failed render with a modal
    /// message box - a dialog nobody is there to dismiss would hang Rhino behind it.
    /// <para>
    /// Fitted to the whole document for the capture and the view put back afterwards, on the same principle
    /// as the viewport screenshot: the canvas belongs to the human.
    /// </para>
    /// </remarks>
    private static string CanvasImage(HttpListenerRequest request)
    {
        int width = int.TryParse(request.QueryString["width"], out int asked)
            ? Math.Clamp(asked, 240, 2400)
            : 1200;

        bool fit = !string.Equals(request.QueryString["fit"], "false", StringComparison.OrdinalIgnoreCase);

        string png = OnUi(() =>
        {
            Grasshopper.GUI.Canvas.GH_Canvas canvas = global::Grasshopper.Instances.ActiveCanvas
                ?? throw new InvalidOperationException("There is no canvas - a headless session has no view.");

            float keptZoom = canvas.Viewport.Zoom;
            System.Drawing.PointF keptMid = canvas.Viewport.MidPoint;

            if (fit && canvas.Document is { } document && document.ObjectCount > 0)
            {
                System.Drawing.RectangleF? all = null;

                foreach (IGH_DocumentObject thing in document.Objects)
                {
                    if (thing.Attributes is { } attributes)
                    {
                        all = all is null
                            ? attributes.Bounds
                            : System.Drawing.RectangleF.Union(all.Value, attributes.Bounds);
                    }
                }

                if (all is { } bounds)
                {
                    bounds.Inflate(40, 40);

                    canvas.Viewport.Zoom = Math.Clamp(
                        Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height),
                        0.05f,
                        Grasshopper.GUI.Canvas.GH_Viewport.ZoomDefault);

                    canvas.Viewport.MidPoint = new System.Drawing.PointF(
                        bounds.X + (bounds.Width / 2),
                        bounds.Y + (bounds.Height / 2));
                }
            }

            // White for the capture: the canvas's own grey wash turns to mud at a tenth of the size, and a
            // picture meant for judging a layout should show the layout. Grasshopper's skin is static, so
            // it is put back immediately afterwards.
            System.Drawing.Color keptBack = Grasshopper.GUI.Canvas.GH_Skin.canvas_back;
            System.Drawing.Color keptGrid = Grasshopper.GUI.Canvas.GH_Skin.canvas_grid;
            System.Drawing.Color keptEdge = Grasshopper.GUI.Canvas.GH_Skin.canvas_edge;

            try
            {
                Grasshopper.GUI.Canvas.GH_Skin.canvas_back = System.Drawing.Color.White;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_grid = System.Drawing.Color.FromArgb(16, 0, 0, 0);
                Grasshopper.GUI.Canvas.GH_Skin.canvas_edge = System.Drawing.Color.White;

                canvas.Refresh();

                using System.Drawing.Bitmap full = new(canvas.Width, canvas.Height);

                canvas.DrawToBitmap(full, new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height));

                int height = Math.Max(120, (int)((double)width / Math.Max(1, full.Width) * full.Height));

                // Onto white: the canvas grid is a pale wash that turns to mud when scaled down, and a
                // picture meant for judging a layout should show the layout, not the tablecloth.
                using System.Drawing.Bitmap scaled = new(width, height);

                using (System.Drawing.Graphics paint = System.Drawing.Graphics.FromImage(scaled))
                {
                    paint.Clear(System.Drawing.Color.White);
                    paint.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    paint.DrawImage(full, 0, 0, width, height);
                }

                using MemoryStream bytes = new();

                scaled.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);

                return Convert.ToBase64String(bytes.ToArray());
            }
            finally
            {
                Grasshopper.GUI.Canvas.GH_Skin.canvas_back = keptBack;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_grid = keptGrid;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_edge = keptEdge;

                canvas.Viewport.Zoom = keptZoom;
                canvas.Viewport.MidPoint = keptMid;
                canvas.Refresh();
            }
        });

        return $"{{\"ok\":true,\"png\":{Json.Quote(png)}}}";
    }

    /// <summary>The eyes, kept cheap on purpose: a low resolution says plenty and costs the reader little.</summary>
    private static string Screenshot(HttpListenerRequest request)
    {
        int width = int.TryParse(request.QueryString["width"], out int asked)
            ? Math.Clamp(asked, 160, 1920)
            : 640;

        bool frame = !string.Equals(request.QueryString["zoomExtents"], "false", StringComparison.OrdinalIgnoreCase);

        string png = OnUi(() =>
        {
            Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc?.Views.ActiveView
                ?? throw new InvalidOperationException("There is no Rhino view to capture.");

            System.Drawing.Size full = view.ClientRectangle.Size;
            int height = Math.Max(120, (int)((double)width / Math.Max(1, full.Width) * Math.Max(1, full.Height)));

            // Framed for the capture, put back after: the picture should show the geometry, but the
            // camera belongs to the human and stays where they left it.
            Rhino.DocObjects.ViewportInfo? kept = frame
                ? new Rhino.DocObjects.ViewportInfo(view.ActiveViewport)
                : null;

            // The target is kept apart: restoring the projection alone recomputes it from the frustum, and
            // the human would come back to their own camera aimed somewhere new.
            Rhino.Geometry.Point3d target = view.ActiveViewport.CameraTarget;

            if (frame)
            {
                view.ActiveViewport.ZoomExtents();
            }

            try
            {
                using System.Drawing.Bitmap bitmap = view.CaptureToBitmap(new System.Drawing.Size(width, height))
                    ?? throw new InvalidOperationException("The viewport would not be captured.");

                using MemoryStream bytes = new();

                bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);

                return Convert.ToBase64String(bytes.ToArray());
            }
            finally
            {
                if (kept is not null)
                {
                    view.ActiveViewport.SetViewProjection(kept, updateTargetLocation: false);
                    view.ActiveViewport.SetCameraTarget(target, updateCameraLocation: false);
                    view.Redraw();
                }
            }
        });

        return $"{{\"ok\":true,\"png\":{Json.Quote(png)}}}";
    }

    /// <summary>The whole of one parameter's data, branch by branch - the numbers an assertion stands on.</summary>
    private static string Peek(HttpListenerRequest request)
    {
        Guid id = Guid.Parse(request.QueryString["id"] ?? throw new ArgumentException("peek needs ?id=guid."));
        string? side = request.QueryString["side"];
        string? param = request.QueryString["param"];

        return OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
                ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

            // A group is a function, so peeking at one answers with its type as it stands: every port, and
            // the shape of the data on each. The alternative was a verb of its own, but every tool costs
            // its description in every session whether or not anybody calls it - and this is the same
            // question, "what data is here", asked of a bigger thing.
            if (thing is Grasshopper.Kernel.Special.GH_Group group)
            {
                return PeekGroup(group, document);
            }

            IGH_Param parameter = LocateBy(thing, side, param);

            System.Text.StringBuilder json = new("{\"ok\":true,\"count\":");

            json.Append(Json.Number(parameter.VolatileDataCount)).Append(",\"branches\":[");

            const int Kept = 500;
            int written = 0;
            bool firstBranch = true;

            foreach (Grasshopper.Kernel.Data.GH_Path path in parameter.VolatileData.Paths)
            {
                if (!firstBranch)
                {
                    json.Append(',');
                }

                firstBranch = false;
                json.Append("{\"path\":").Append(Json.Quote(path.ToString())).Append(",\"values\":[");

                System.Collections.IList branch = parameter.VolatileData.get_Branch(path);
                bool firstValue = true;

                foreach (object? item in branch)
                {
                    if (written >= Kept)
                    {
                        break;
                    }

                    if (!firstValue)
                    {
                        json.Append(',');
                    }

                    firstValue = false;
                    written++;
                    json.Append(Json.Quote((item as Grasshopper.Kernel.Types.IGH_Goo)?.ToString() ?? item?.ToString() ?? "null"));
                }

                json.Append("]}");

                if (written >= Kept)
                {
                    break;
                }
            }

            json.Append(']');

            if (written >= Kept)
            {
                json.Append(",\"truncated\":true");
            }

            return json.Append('}').ToString();
        });
    }

    /// <summary>
    /// A group's current signature, measured: every inlet and outlet, with the branch and item counts that
    /// are the specification, and a few values off each outlet so a result can be recognised.
    /// </summary>
    /// <remarks>
    /// Counts rather than full data on purpose. Peeking at a group with six outlets of a thousand branches
    /// each would flood the very context this verb exists to protect - and the counts are what an assertion
    /// is written against anyway. Whoever needs the values takes the port's own id and peeks at that.
    /// </remarks>
    private static string PeekGroup(Grasshopper.Kernel.Special.GH_Group group, GH_Document document)
    {
        (List<IGH_Param> inlets, List<IGH_Param> outlets) = Signature.Ports(document, group);

        System.Text.StringBuilder json = new("{\"ok\":true,\"group\":");

        json.Append(Json.Quote(string.IsNullOrWhiteSpace(group.NickName) ? "(unnamed)" : group.NickName));

        void Side(string name, List<IGH_Param> ports, bool withSample)
        {
            json.Append(",\"").Append(name).Append("\":[");

            for (int at = 0; at < ports.Count; at++)
            {
                IGH_Param port = ports[at];

                if (at > 0)
                {
                    json.Append(',');
                }

                json.Append("{\"name\":").Append(Json.Quote(
                    string.IsNullOrWhiteSpace(port.NickName) ? port.Name : port.NickName));
                json.Append(",\"id\":").Append(Json.Quote(port.InstanceGuid.ToString()));
                json.Append(",\"type\":").Append(Json.Quote(port.TypeName));
                json.Append(",\"count\":").Append(Json.Number(port.VolatileDataCount));
                json.Append(",\"branches\":").Append(Json.Number(port.VolatileData.PathCount));

                if (withSample)
                {
                    json.Append(",\"sample\":[");

                    int taken = 0;

                    foreach (Grasshopper.Kernel.Data.GH_Path path in port.VolatileData.Paths)
                    {
                        foreach (object? item in port.VolatileData.get_Branch(path))
                        {
                            if (taken >= 3)
                            {
                                break;
                            }

                            if (taken > 0)
                            {
                                json.Append(',');
                            }

                            taken++;
                            json.Append(Json.Quote(
                                (item as Grasshopper.Kernel.Types.IGH_Goo)?.ToString() ?? item?.ToString() ?? "null"));
                        }

                        if (taken >= 3)
                        {
                            break;
                        }
                    }

                    json.Append(']');
                }

                json.Append('}');
            }

            json.Append(']');
        }

        Side("inlets", inlets, withSample: false);
        Side("outlets", outlets, withSample: true);

        // Said out loud rather than left to be inferred from two empty arrays: a group with no ports has
        // either not been signed yet or is not a function, and both are worth knowing before reading on.
        if (inlets.Count == 0 && outlets.Count == 0)
        {
            json.Append(",\"note\":\"no ports - this group has no signature yet; call signature first\"");
        }

        return json.Append('}').ToString();
    }

    private static string Place(JsonDocument request)
    {
        string author = Author(request);

        if (!request.RootElement.TryGetProperty("objects", out JsonElement objects))
        {
            throw new ArgumentException("place needs 'objects'.");
        }

        string mapping = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to place into.");

            EnsureAutosave(document);

            // Where a body goes when the recipe does not say: inside its own group's lane, in rows. Without
            // this every object lands on the origin in one unreadable pile, which is no use to a human
            // watching the build and hoping to intervene before it is finished.
            Grasshopper.Kernel.Special.GH_Group? host =
                Field(request, "group") is { } intoGroup
                    ? document.FindObject(Guid.Parse(intoGroup), topLevelOnly: true)
                        as Grasshopper.Kernel.Special.GH_Group
                        ?? throw new KeyNotFoundException($"No group {intoGroup} on the canvas.")
                    : null;

            System.Drawing.PointF lane = host?.Attributes?.Bounds is { } frame
                ? new System.Drawing.PointF(frame.Left + 170, frame.Top + 10)
                : new System.Drawing.PointF(100, 100 + (document.ObjectCount * 4));

            int laid = 0;

            // Every proxy resolved before a single object is added: a recipe either lands whole or leaves
            // the canvas exactly as it was.
            List<(IGH_ObjectProxy Proxy, JsonElement Spec)> recipe = [.. objects.EnumerateArray()
                .Select(spec => (Resolve(spec), spec))];

            // First pass: everything stands, configured, and the recipe's local ids learn their real ones.
            Dictionary<string, IGH_DocumentObject> made = [];

            foreach ((IGH_ObjectProxy proxy, JsonElement spec) in recipe)
            {
                IGH_DocumentObject thing = Instantiate(proxy, spec, new System.Drawing.PointF(
                    lane.X + (laid % 5 * 150),
                    lane.Y + (laid++ / 5 * 80)));

                document.AddObject(thing, update: false);
                document.UndoUtil.RecordAddObjectEvent("Phenome Link: place", thing);

                Configure(thing, spec);

                made[spec.TryGetProperty("id", out JsonElement local) && local.GetString() is { } key
                    ? key
                    : thing.InstanceGuid.ToString()] = thing;
            }

            // Second pass: the wires, now that both ends exist. A source id is a recipe-local key first
            // and an existing canvas guid second, so a recipe can graft onto what is already there.
            foreach (JsonElement spec in objects.EnumerateArray())
            {
                if (!spec.TryGetProperty("inputs", out JsonElement inputs))
                {
                    continue;
                }

                if (!spec.TryGetProperty("id", out JsonElement local) || local.GetString() is not { } key)
                {
                    throw new ArgumentException("an object with 'inputs' needs an 'id' to be found by.");
                }

                if (!made.TryGetValue(key, out IGH_DocumentObject? target))
                {
                    throw new KeyNotFoundException($"'{key}' has inputs but no object of that local id was placed.");
                }

                foreach (JsonElement input in inputs.EnumerateArray())
                {
                    string? which = input.TryGetProperty("param", out JsonElement named) ? named.ToString() : null;
                    IGH_Param sink = LocateBy(target, "input", which);

                    // A constant typed straight into the socket, which is what a caller means by a value
                    // on an input and what this refused - with a dictionary's error message, no less,
                    // because a missing 'sources' was read as a missing key rather than a shape it knows.
                    if (input.TryGetProperty("value", out JsonElement constant))
                    {
                        Store(sink, constant);
                        continue;
                    }

                    if (!input.TryGetProperty("sources", out JsonElement sources))
                    {
                        throw new ArgumentException(
                            $"input '{which ?? "0"}' of '{Named(target)}' needs either 'sources' (wires) or "
                            + "'value' (a constant typed into the socket).");
                    }

                    foreach (JsonElement source in sources.EnumerateArray())
                    {
                        if (!source.TryGetProperty("id", out JsonElement fromId))
                        {
                            throw new ArgumentException(
                                $"a source of '{Named(target)}' input '{which ?? "0"}' has no 'id'.");
                        }

                        string from = fromId.GetString()!;

                        IGH_DocumentObject owner = made.TryGetValue(from, out IGH_DocumentObject? fresh)
                            ? fresh
                            : document.FindObject(Guid.Parse(from), topLevelOnly: true)
                                ?? throw new KeyNotFoundException($"'{from}' is neither in the recipe nor on the canvas.");

                        sink.AddSource(LocateBy(
                            owner,
                            "output",
                            source.TryGetProperty("output", out JsonElement outputAt) ? outputAt.ToString() : null));
                    }
                }
            }

            // Placed straight into the group that asked for them: in a signature-first build, the body
            // belongs to the function whose signature it fills, and saying so here saves an extra call
            // and the "ungrouped objects" the review would otherwise, rightly, complain about.
            if (host is not null)
            {
                foreach (IGH_DocumentObject thing in made.Values)
                {
                    host.AddObject(thing.InstanceGuid);
                }

                host.ExpireCaches();
            }

            document.NewSolution(false);

            System.Text.StringBuilder json = new("{\"ok\":true,\"placed\":{");
            bool first = true;

            foreach ((string key, IGH_DocumentObject thing) in made)
            {
                if (!first)
                {
                    json.Append(',');
                }

                first = false;
                json.Append(Json.Quote(key)).Append(':').Append(Json.Quote(thing.InstanceGuid.ToString()));
            }

            return json.Append("}}").ToString();
        });

        Journal.Append(author, "place", $",\"objects\":{Json.Number(objects.GetArrayLength())}");

        return mapping;
    }

    /// <summary>
    /// The proxy a recipe entry names - by guid, or by a name that must mean exactly one thing.
    /// </summary>
    /// <remarks>
    /// Resolved for the whole recipe before anything is added to the document, so a name nobody recognises
    /// fails on an untouched canvas instead of leaving twenty objects standing and the twenty-first missing.
    /// An ambiguous name is refused with the candidates rather than silently picking one: "Merge" is two
    /// different components with different parameter names, and guessing between them is not this server's
    /// business.
    /// </remarks>
    private static IGH_ObjectProxy Resolve(JsonElement spec)
    {
        if (spec.TryGetProperty("guid", out JsonElement guid))
        {
            return global::Grasshopper.Instances.ComponentServer.EmitObjectProxy(Guid.Parse(guid.GetString()!))
                ?? throw new KeyNotFoundException($"No component with guid {guid.GetString()}.");
        }

        if (!spec.TryGetProperty("name", out JsonElement name))
        {
            throw new ArgumentException("each placed object needs 'name' or 'guid'.");
        }

        string asked = name.GetString()!;

        List<IGH_ObjectProxy> found = [.. global::Grasshopper.Instances.ComponentServer.ObjectProxies
            .Where(candidate =>
                !candidate.Obsolete
                && string.Equals(candidate.Desc.Name, asked, StringComparison.OrdinalIgnoreCase))];

        if (found.Count == 0)
        {
            throw new KeyNotFoundException($"No component is called '{asked}'.");
        }

        if (found.Count > 1)
        {
            string candidates = string.Join(", ", found.Select(one =>
                $"{one.Desc.Name} [{one.Desc.Category} › {one.Desc.SubCategory}] {one.Guid}"));

            throw new ArgumentException(
                $"'{asked}' names {found.Count} different components - say which by guid: {candidates}");
        }

        return found[0];
    }

    /// <summary>One recipe entry into a live object, from a proxy already resolved.</summary>
    private static IGH_DocumentObject Instantiate(
        IGH_ObjectProxy proxy,
        JsonElement spec,
        System.Drawing.PointF fallback)
    {
        IGH_DocumentObject thing = proxy.CreateInstance()
            ?? throw new InvalidOperationException($"{proxy.Desc.Name} would not instantiate.");

        if (spec.TryGetProperty("nickname", out JsonElement nickname))
        {
            thing.NickName = nickname.GetString() ?? thing.NickName;
        }

        thing.CreateAttributes();

        thing.Attributes.Pivot = spec.TryGetProperty("pivot", out JsonElement pivot)
            ? new System.Drawing.PointF((float)AsDouble(pivot[0]), (float)AsDouble(pivot[1]))
            : fallback;

        return thing;
    }

    /// <summary>The values a recipe entry carries: a slider's domain, a panel's text, a stored value.</summary>
    private static void Configure(IGH_DocumentObject thing, JsonElement spec)
    {
        if (thing is Grasshopper.Kernel.Special.GH_NumberSlider slider
            && spec.TryGetProperty("slider", out JsonElement domain))
        {
            if (domain.TryGetProperty("minimum", out JsonElement minimum))
            {
                slider.Slider.Minimum = (decimal)AsDouble(minimum);
            }

            if (domain.TryGetProperty("maximum", out JsonElement maximum))
            {
                slider.Slider.Maximum = (decimal)AsDouble(maximum);
            }

            if (domain.TryGetProperty("decimals", out JsonElement decimals))
            {
                slider.Slider.DecimalPlaces = (int)AsDouble(decimals);
                slider.Slider.Type = (int)AsDouble(decimals) == 0
                    ? Grasshopper.GUI.Base.GH_SliderAccuracy.Integer
                    : Grasshopper.GUI.Base.GH_SliderAccuracy.Float;
            }

            if (domain.TryGetProperty("value", out JsonElement at))
            {
                slider.SetSliderValue((decimal)AsDouble(at));
            }

            return;
        }

        if (thing is Grasshopper.Kernel.Special.GH_Panel panel
            && spec.TryGetProperty("text", out JsonElement text))
        {
            panel.UserText = text.GetString() ?? "";
            return;
        }

        if (thing is IGH_Param parameter && spec.TryGetProperty("value", out JsonElement value))
        {
            Store(parameter, value);
        }
    }

    private static string Save(JsonDocument request)
    {
        string author = Author(request);
        string? asked = Field(request, "path");

        string path = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document to save.");

            string target = asked
                ?? document.FilePath
                ?? throw new ArgumentException("The document was never saved - say where with 'path'.");

            WriteDocument(document, target);
            document.FilePath = target;

            return target;
        });

        Journal.Append(author, "save", $",\"path\":{Json.Quote(path)}");

        return $"{{\"ok\":true,\"path\":{Json.Quote(path)}}}";
    }

    private static readonly HashSet<Guid> Autosaved = [];

    /// <summary>
    /// A copy into %TEMP% before an agent's first edit of a document - the net under the undo stack.
    /// </summary>
    private static void EnsureAutosave(GH_Document document)
    {
        if (!Autosaved.Add(document.DocumentID))
        {
            return;
        }

        try
        {
            WriteDocument(document, Path.Combine(
                Path.GetTempPath(),
                $"phenome-autosave-{document.DocumentID:N}.gh"));
        }
        catch (Exception failure)
        {
            LinkLog.Say($"Phenome Link: autosave failed ({failure.Message}); carrying on.");
        }
    }

    /// <summary>Serialised via the archive, which unlike a Save never touches the document's own path.</summary>
    private static void WriteDocument(GH_Document document, string path)
    {
        GH_IO.Serialization.GH_Archive archive = new();

        if (!archive.AppendObject(document, "Definition"))
        {
            throw new InvalidOperationException("The document would not serialise.");
        }

        if (!archive.WriteToFile(path, true, false))
        {
            throw new InvalidOperationException($"Could not write {path}.");
        }
    }

    private static string Zoom(JsonDocument request)
    {
        string author = Author(request);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("zoom needs 'ids'.");

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            Grasshopper.GUI.Canvas.GH_Canvas canvas = global::Grasshopper.Instances.ActiveCanvas
                ?? throw new InvalidOperationException("There is no canvas to move - headless sessions have no view.");

            System.Drawing.RectangleF? union = null;

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { Attributes: { } attributes })
                {
                    union = union is null
                        ? attributes.Bounds
                        : System.Drawing.RectangleF.Union(union.Value, attributes.Bounds);
                }
            }

            if (union is not { } bounds)
            {
                throw new KeyNotFoundException("None of those ids are on the canvas.");
            }

            bounds.Inflate(40, 40);

            canvas.Viewport.Zoom = Math.Clamp(
                Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height),
                0.1f,
                Grasshopper.GUI.Canvas.GH_Viewport.ZoomDefault);

            canvas.Viewport.MidPoint = new System.Drawing.PointF(
                bounds.X + (bounds.Width / 2),
                bounds.Y + (bounds.Height / 2));

            canvas.Refresh();

            return true;
        });

        Journal.Append(author, "zoom", $",\"count\":{Json.Number(asked.Count)}");

        return "{\"ok\":true}";
    }

    private static string RunScript(JsonDocument request)
    {
        string author = Author(request);
        string script = Field(request, "script") ?? throw new ArgumentException("rhino needs 'script'.");

        bool ran = OnUi(() => Rhino.RhinoApp.RunScript(script, echo: true));

        Journal.Append(author, "rhino", $",\"script\":{Json.Quote(script)},\"ok\":{(ran ? "true" : "false")}");

        return $"{{\"ok\":{(ran ? "true" : "false")}}}";
    }

    private static string RhinoSummary()
    {
        Rhino.RhinoDoc doc = Rhino.RhinoDoc.ActiveDoc
            ?? throw new InvalidOperationException("There is no Rhino document.");

        System.Text.StringBuilder json = new("{\"name\":");

        json.Append(Json.Quote(string.IsNullOrEmpty(doc.Name) ? "unsaved" : doc.Name));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));

        // Where the human is looking, so an empty screenshot can be diagnosed rather than guessed at.
        if (doc.Views.ActiveView is { } view)
        {
            Rhino.Geometry.Point3d eye = view.ActiveViewport.CameraLocation;
            Rhino.Geometry.Point3d at = view.ActiveViewport.CameraTarget;

            json.Append(",\"camera\":{\"name\":").Append(Json.Quote(view.ActiveViewport.Name ?? ""));
            json.Append(",\"eye\":[").Append(Json.Number((long)eye.X)).Append(',')
                .Append(Json.Number((long)eye.Y)).Append(',').Append(Json.Number((long)eye.Z)).Append(']');
            json.Append(",\"target\":[").Append(Json.Number((long)at.X)).Append(',')
                .Append(Json.Number((long)at.Y)).Append(',').Append(Json.Number((long)at.Z)).Append("]}");
        }

        json.Append(",\"layers\":[");

        bool first = true;

        foreach (Rhino.DocObjects.Layer layer in doc.Layers)
        {
            if (layer.IsDeleted)
            {
                continue;
            }

            if (!first)
            {
                json.Append(',');
            }

            first = false;
            json.Append("{\"path\":").Append(Json.Quote(layer.FullPath));

            if (!layer.IsVisible)
            {
                json.Append(",\"visible\":false");
            }

            if (layer.IsLocked)
            {
                json.Append(",\"locked\":true");
            }

            json.Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>One end of a wire: the object, and when it is a component, which of its parameters.</summary>
    private static IGH_Param End(GH_Document document, JsonElement request, string which, bool outputSide)
    {
        if (!request.TryGetProperty(which, out JsonElement end))
        {
            throw new ArgumentException($"wire needs '{which}'.");
        }

        Guid id = Guid.Parse(end.GetProperty("id").GetString()!);

        IGH_DocumentObject thing = document.FindObject(id, topLevelOnly: true)
            ?? throw new KeyNotFoundException($"No object {id} on the canvas.");

        if (thing is IGH_Param loose)
        {
            return loose;
        }

        if (thing is not IGH_Component component)
        {
            throw new ArgumentException($"{thing.Name} has no parameters to wire.");
        }

        List<IGH_Param> side = outputSide ? component.Params.Output : component.Params.Input;

        if (!end.TryGetProperty("param", out JsonElement param))
        {
            return side.Count == 1
                ? side[0]
                : throw new ArgumentException(
                    $"{component.Name} has {side.Count} on that side - say which with 'param'.");
        }

        // "0" is an index whether the client sent a number or a string of one - MCP clients do both.
        string asked = param.ValueKind == JsonValueKind.Number
            ? param.GetRawText()
            : param.GetString()!;

        if (int.TryParse(asked, out int index))
        {
            return index >= 0 && index < side.Count
                ? side[index]
                : throw new ArgumentException($"{component.Name} has {side.Count} on that side; {index} is not one of them.");
        }

        return side.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, asked, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.NickName, asked, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"{component.Name} has no parameter '{asked}'.");
    }

    /// <summary>
    /// A value into a parameter's own storage, replacing whatever was there. A null empties it.
    /// </summary>
    /// <remarks>
    /// Cleared first, because <c>SetPersistentData</c> appends despite its name - and Grasshopper's own
    /// defaults are persistent data too. Setting 0 on a socket that already defaulted to 0 therefore left
    /// two zeroes, and a component fed two values emits two branches: a definition silently doubled its
    /// geometry. Found in the field by an agent, which is what the friction log is for.
    /// </remarks>
    private static void Store(IGH_Param parameter, JsonElement value)
    {
        parameter.GetType()
            .GetMethod("Script_ClearPersistentData", Type.EmptyTypes)
            ?.Invoke(parameter, null);

        if (value.ValueKind == JsonValueKind.Null)
        {
            // A null is how a caller empties a socket - the only way back to "nothing stored here".
            return;
        }

        object raw = value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new ArgumentException("set takes a number, text, a flag, or null to empty the socket."),
        };

        // SetPersistentData(params object[]) lives on GH_PersistentParam<T>; reflection reaches it on the
        // concrete type, and refusal by name beats silently doing nothing.
        System.Reflection.MethodInfo? set = parameter.GetType().GetMethod("SetPersistentData", [typeof(object[])]);

        if (set is null)
        {
            throw new ArgumentException($"{parameter.Name} does not store values.");
        }

        set.Invoke(parameter, [new[] { raw }]);
    }

    // ---- Plumbing --------------------------------------------------------------------------------------

    // The canvas first, the server second: headless Rhino has documents but no canvas to call active.
    private static GH_Document? ActiveDocument() =>
        global::Grasshopper.Instances.ActiveCanvas?.Document
        ?? global::Grasshopper.Instances.DocumentServer.FirstOrDefault();

    private static long Since(HttpListenerRequest request) =>
        long.TryParse(request.QueryString["since"], out long since) ? since : 0;

    private static string ReadBody(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, Encoding.UTF8);

        return reader.ReadToEnd();
    }

    private static JsonDocument Read(string payload) => JsonDocument.Parse(
        string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

    private static string Author(JsonDocument request) => Field(request, "author") ?? "unnamed";

    /// <summary>
    /// A flag, however the client spelt it. MCP clients routinely serialise scalars as strings, so a
    /// server that insists on JSON booleans punishes the wrong party.
    /// </summary>
    private static bool AsBool(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetDouble() != 0,
        JsonValueKind.String => value.GetString()!.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "on" => true,
            "false" or "0" or "no" or "off" => false,
            _ => throw new ArgumentException($"'{value.GetString()}' is not a flag."),
        },
        _ => throw new ArgumentException("Expected a flag."),
    };

    /// <summary>A colour, however the client spelt it: [r,g,b], "255,60,60", or "#ff3c3c".</summary>
    private static System.Drawing.Color AsColour(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 3)
        {
            return System.Drawing.Color.FromArgb(
                (int)AsDouble(value[0]),
                (int)AsDouble(value[1]),
                (int)AsDouble(value[2]));
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException("a colour is [r,g,b], \"r,g,b\" or \"#rrggbb\".");
        }

        string said = value.GetString()!.Trim();

        if (said.StartsWith('#'))
        {
            return System.Drawing.ColorTranslator.FromHtml(said);
        }

        string[] parts = said.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        return parts.Length >= 3
            ? System.Drawing.Color.FromArgb(int.Parse(parts[0]), int.Parse(parts[1]), int.Parse(parts[2]))
            : System.Drawing.ColorTranslator.FromHtml(said);
    }

    /// <summary>A number, however the client spelt it - same story as <see cref="AsBool"/>.</summary>
    private static double AsDouble(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.String when double.TryParse(
            value.GetString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out double parsed) => parsed,
        _ => throw new ArgumentException("Expected a number."),
    };

    /// <summary>A text field of one object, whether it is a whole request or one entry of a batch.</summary>
    private static string? Text(JsonElement request, string name) =>
        request.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static string? Field(JsonDocument request, string name) =>
        request.RootElement.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    /// <summary>
    /// Runs work on the Rhino UI thread and waits for the answer - the document belongs to that thread,
    /// and the listener lives on none in particular.
    /// </summary>
    private static T OnUi<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        using SemaphoreSlim done = new(0, 1);

        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception thrown)
            {
                failure = thrown;
            }
            finally
            {
                done.Release();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(15)))
        {
            // Not "it did not answer" - that sentence is true of a long solve and of a modal alike, and
            // those want opposite responses. Pulse can tell them apart without the thread this is waiting
            // for, so the refusal says which one happened.
            throw new TimeoutException(Pulse.Sentence());
        }

        return failure is null ? result : throw failure;
    }

    private static int FreePort()
    {
        // The system picks a free port; HttpListener cannot ask for one itself, so a socket asks and lets go.
        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }
}
