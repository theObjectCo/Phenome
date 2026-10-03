using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// The loopback interface to a Rhino that was never opened.
/// </summary>
/// <remarks>
/// Follows the conventions of the other two links (plain HTTP on 127.0.0.1, one JSON out, an ephemeral port in a
/// discovery file named by process id), and a client that speaks to those speaks to this. What it reports
/// differs, because there is no canvas and no document being viewed: the file on disk is the state, and every
/// verb names its own.
/// <para>
/// The verb list is short because it reflects what a headless core can do. Rhino commands do not run:
/// <c>RunScript</c> returns false and changes nothing, through both the serial-number overload against a
/// headless document and against one opened normally (headless in this process regardless). There is no
/// <c>/command</c> here for that reason; the process link next door covers it. Reading a document, writing it,
/// and Rhino's importers and exporters do work, and those load in a windowless Rhino.
/// </para>
/// </remarks>
internal static class InsideServer
{
    static HttpListener? listener;
    static HeadlessRhino? rhino;
    static readonly Stopwatch Uptime = Stopwatch.StartNew();

    static long served;
    static long dropped;
    static string? running;
    static long runningSince;

    internal static int Port { get; private set; }

    const string Description = """
        {
          "phenome": "rhino-inside-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /pulse": "whether the core is free, what verb it is on and for how long, uptime, and how many requests were served and dropped. Answered without the queue; it answers while the queue is busy",
            "GET /doc": "?path=<.3dm> - what a document holds: units, tolerance, layers, and a count of each kind of object",
            "POST /convert": "{from, to, version?} - read one file and write another. The target's extension picks the format: .3dm through the archive writer, anything else through Rhino's exporter for it. Verified headless for .stl, .obj, .dxf and .step. 'version' applies to .3dm only; 0 means current",
            "POST /quit": "stop serving and let the process end"
          },
          "why": "The other two links live inside a Rhino the user has opened. This one starts a Rhino core in its own process with no window, and a document can be read or converted without a splash screen appearing.",
          "what it cannot do": "Rhino commands. RunScript answers false in a windowless core, and anything that is a command (selection, export options, most of the toolbar) is out of reach here. Use the process link inside a real Rhino for that.",
          "discovery": "%TEMP%/phenome-rhinoinside-<pid>.port holds this port"
        }
        """;

    internal static void Start(HeadlessRhino core)
    {
        rhino = core;

        listener = Loopback.Listen(out int port);
        Port = port;

        WritePortFile();

        Task.Run(async () =>
        {
            while (listener is not null && listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = await listener.GetContextAsync();
                    _ = Task.Run(() => Answer(context));
                }
                catch (Exception) when (listener is null || !listener.IsListening)
                {
                    // The listener was shut down mid-await, which is not an error.
                }
                catch (Exception)
                {
                    // No useful place to report it: the console belongs to the user who started the process, and a
                    // listener that fails on one request should still accept the next.
                }
            }
        });
    }

    internal static void Stop()
    {
        try
        {
            listener?.Stop();
            listener?.Close();
        }
        catch (Exception)
        {
            // Shutting down; nothing to report.
        }
        finally
        {
            listener = null;
            DeletePortFile();
        }
    }

    static void Answer(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string method = context.Request.HttpMethod;

        // A page the user visits can reach loopback, and binding to 127.0.0.1 does not protect against it (see
        // Browser.Refuse).
        if (Browser.Refuse(context.Request, Port) is { } refused)
        {
            Respond(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(refused)}}}");
            return;
        }

        if (Advisory.Withdrawn is { } notice && path.Length != 0)
        {
            Respond(context.Response, 403, $"{{\"ok\":false,\"error\":{Json.Quote(notice.Sentence)}}}");
            return;
        }

        string payload = method == "POST" ? ReadBody(context.Request) : "";

        served++;

        try
        {
            string body = (method, path) switch
            {
                ("GET", "") => Description,
                ("GET", "/pulse") => Pulse(),
                ("GET", "/doc") => Work("doc", () => Documents.Describe(
                    context.Request.QueryString["path"]
                        ?? throw new ArgumentException("doc needs ?path=<a .3dm>."))),
                ("POST", "/convert") => Converted(payload),
                ("POST", "/quit") => Quit(),
                _ => throw new KeyNotFoundException($"There is no {method} {path}. GET / describes what there is."),
            };

            Send(context.Response, 200, body);
        }
        catch (KeyNotFoundException missing)
        {
            Send(context.Response, 404, Refusal(missing));
        }
        catch (FileNotFoundException missing)
        {
            Send(context.Response, 404, Refusal(missing));
        }
        catch (Exception asked) when (asked is ArgumentException or JsonException)
        {
            // A missing field or a non-JSON body is a bad request, not a server failure. The distinction tells
            // a client whether to fix its call or retry later.
            Send(context.Response, 400, Refusal(asked));
        }
        catch (Exception failure)
        {
            Send(context.Response, 500, Refusal(failure));
        }
    }

    /// <summary>
    /// Runs a verb on the thread that owns the Rhino core, recording what is running while it does.
    /// </summary>
    /// <remarks>
    /// The name and elapsed time are what <c>/pulse</c> reads, and are the only way a caller can distinguish a
    /// long conversion from a hang. The record is kept here, in one place for every verb: a verb that did not
    /// record its work would be invisible exactly when someone is asking about it.
    /// </remarks>
    static string Work(string verb, Func<string> work)
    {
        HeadlessRhino core = rhino ?? throw new InvalidOperationException("The Rhino core is not running.");

        running = verb;
        runningSince = Uptime.ElapsedMilliseconds;

        try
        {
            return core.Invoke(work);
        }
        finally
        {
            running = null;
        }
    }

    static string Converted(string payload)
    {
        using JsonDocument request = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

        string from = Json.Text(request, "from") ?? throw new ArgumentException("convert needs 'from'.");
        string to = Json.Text(request, "to") ?? throw new ArgumentException("convert needs 'to'.");
        int version = Json.Int(request, "version", 0);

        return Work("convert", () => Documents.Convert(from, to, version));
    }

    /// <summary>
    /// The state, computed without touching the queue. It answers while the queue is busy.
    /// </summary>
    /// <remarks>
    /// The same guarantee the process link makes about the UI thread, for the same reason: when someone wants to
    /// know whether anything is happening, everything else is blocked. It is cheap here because the running verb
    /// is a field this server sets, and Rhino is not asked.
    /// </remarks>
    static string Pulse()
    {
        string? verb = running;
        long since = verb is null ? 0 : Uptime.ElapsedMilliseconds - runningSince;

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"state\":").Append(Json.Quote(verb is null ? "idle" : "busy"));
        json.Append(",\"upForMs\":").Append(Json.Number(Uptime.ElapsedMilliseconds));
        json.Append(",\"served\":").Append(Json.Number(served));
        json.Append(",\"dropped\":").Append(Json.Number(dropped));
        json.Append(",\"headless\":").Append(Rhino.RhinoApp.IsRunningHeadless ? "true" : "false");

        if (verb is not null)
        {
            json.Append(",\"verb\":").Append(Json.Quote(verb));
            json.Append(",\"forMs\":").Append(Json.Number(since));
        }

        json.Append(",\"advice\":").Append(Json.Quote(verb is null
            ? "The core is free."
            : $"{verb} has been running for {since}ms. Verbs are served one at a time, and anything else is waiting behind it."));

        json.Append('}');

        return json.ToString();
    }

    static string Quit()
    {
        // Answered before anything is torn down so the caller receives a reply. The core stops once this
        // response is sent.
        Task.Run(async () =>
        {
            await Task.Delay(100);
            rhino?.Stop();
        });

        return "{\"ok\":true,\"stopping\":true}";
    }

    static string Refusal(Exception failure) =>
        $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}";

    /// <summary>
    /// Writes the response, treating a write failure as separate from an answer failure.
    /// </summary>
    /// <remarks>
    /// A client that stops waiting makes the write throw. Catching and answering again writes to a closed stream,
    /// which throws with a "response already submitted" message and hides the real error. Writing is the final
    /// step: a failure here is only counted, because the verb already ran and there is no client left to notify.
    /// <para>
    /// The canvas half's <c>Send</c> has the same structure, for the same reason.
    /// </para>
    /// </remarks>
    static void Send(HttpListenerResponse response, int status, string body)
    {
        try
        {
            Respond(response, status, body);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref dropped);
        }
    }

    static string ReadBody(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }

    static void Respond(HttpListenerResponse response, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";

        // No Access-Control-Allow-Origin header, as on the canvas link. Browser.Refuse turns a page's request
        // away, and with "*" here the page could still read that refusal and learn which port answers.
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// The port file, named by this process id. Its name differs from the other two links' names on purpose.
    /// </summary>
    /// <remarks>
    /// A client globs for all three and identifies each by name: a canvas, an interactive Rhino, or a headless
    /// one. A shared name would make them indistinguishable, and they report different things.
    /// </remarks>
    static string PortFile =>
        Path.Combine(Path.GetTempPath(), $"phenome-rhinoinside-{System.Environment.ProcessId}.port");

    static void WritePortFile()
    {
        try
        {
            File.WriteAllText(PortFile, Port.ToString());
        }
        catch (Exception)
        {
            // Without the file, a client must be given the port, which is worse but not fatal.
        }
    }

    static void DeletePortFile()
    {
        try
        {
            if (File.Exists(PortFile)) File.Delete(PortFile);
        }
        catch (Exception)
        {
            // A stale file is caught by the client's pid check; best effort suffices.
        }
    }
}
