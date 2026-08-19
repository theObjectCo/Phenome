using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// The loopback interface to a Rhino that was never opened.
/// </summary>
/// <remarks>
/// Same conventions as the other two links - plain HTTP on 127.0.0.1, one JSON out, an ephemeral port in a
/// discovery file named by process id - so a client that speaks to those speaks to this. What it answers
/// about is different: there is no canvas and no document anybody is looking at, so the file on disk is the
/// state and every verb names its own.
/// <para>
/// The verb list is short because it is honest about what a headless core can do, which was measured rather
/// than assumed. Rhino commands do not run: <c>RunScript</c> answers false and changes nothing, both through
/// the serial-number overload against a headless document and against one opened the ordinary way, which in
/// this process is headless anyway. So there is no <c>/command</c> here, and the process link next door is
/// where that belongs. What does work is reading a document, writing it, and Rhino's importers and exporters,
/// which turn out to load in a Rhino with no window.
/// </para>
/// </remarks>
internal static class InsideServer
{
    static HttpListener? listener;
    static HeadlessRhino? rhino;
    static readonly Stopwatch Uptime = Stopwatch.StartNew();

    static long served;
    static string? running;
    static long runningSince;

    internal static int Port { get; private set; }

    const string Description = """
        {
          "phenome": "rhino-inside-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /pulse": "whether the core is free, what verb it is on and for how long, and how many requests are queued behind it. Answered without the queue, so it answers while the queue is busy",
            "GET /doc": "?path=<.3dm> - what a document holds: units, tolerance, layers, and a count of each kind of object",
            "POST /convert": "{from, to, version?} - read one file and write another. The target's extension picks the format: .3dm through the archive writer, anything else through Rhino's exporter for it. Verified headless for .stl, .obj, .dxf and .step. 'version' applies to .3dm only; 0 means current",
            "POST /quit": "stop serving and let the process end"
          },
          "why": "The other two links live inside a Rhino somebody opened. This one starts a Rhino core in its own process with no window, so a document can be read or converted with nobody watching a splash screen.",
          "what it cannot do": "Rhino commands. RunScript answers false in a windowless core, so anything that is a command - selection, export options, most of the toolbar - is out of reach here. Use the process link inside a real Rhino for that.",
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
                    // Shut down mid-await; not an incident.
                }
                catch (Exception)
                {
                    // Nowhere useful to say it: the console belongs to whoever started the process, and a
                    // listener that stumbles on one request should still take the next.
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
            // Shutting down; nothing left to tell.
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

            Respond(context.Response, 200, body);
        }
        catch (KeyNotFoundException missing)
        {
            Respond(context.Response, 404, Refusal(missing));
        }
        catch (FileNotFoundException missing)
        {
            Respond(context.Response, 404, Refusal(missing));
        }
        catch (Exception asked) when (asked is ArgumentException or JsonException)
        {
            // A field left out or a body that is not JSON is a bad request, not a server that fell over. The
            // distinction is what tells a client whether to fix its call or to try again later.
            Respond(context.Response, 400, Refusal(asked));
        }
        catch (Exception failure)
        {
            Respond(context.Response, 500, Refusal(failure));
        }
    }

    /// <summary>
    /// Runs a verb on the thread Rhino belongs to, remembering what is running while it does.
    /// </summary>
    /// <remarks>
    /// The name and the clock are what <c>/pulse</c> reads, and they are the whole reason a caller can tell a
    /// long conversion from a hung one. Worth having here rather than in each verb: a verb that forgot to say
    /// what it was doing would be invisible in exactly the situation somebody is asking about.
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
    /// The state, computed without touching the queue - so it answers while the queue is busy.
    /// </summary>
    /// <remarks>
    /// That is the same promise the process link makes about the UI thread, for the same reason: the moment
    /// somebody wants to know whether anything is happening is the moment everything else is blocked. Here it
    /// is cheap, because what is running is a field this server sets rather than something to ask Rhino.
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
        json.Append(",\"headless\":").Append(Rhino.RhinoApp.IsRunningHeadless ? "true" : "false");

        if (verb is not null)
        {
            json.Append(",\"verb\":").Append(Json.Quote(verb));
            json.Append(",\"forMs\":").Append(Json.Number(since));
        }

        json.Append(",\"advice\":").Append(Json.Quote(verb is null
            ? "The core is free."
            : $"{verb} has been running for {since}ms. Verbs are served one at a time, so anything else is waiting behind it."));

        json.Append('}');

        return json.ToString();
    }

    static string Quit()
    {
        // Answered before anything is torn down, because the caller asked a question and deserves the answer
        // to arrive. The core stops once this response is on the wire.
        Task.Run(async () =>
        {
            await Task.Delay(100);
            rhino?.Stop();
        });

        return "{\"ok\":true,\"stopping\":true}";
    }

    static string Refusal(Exception failure) =>
        $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}";

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
        response.Headers["Access-Control-Allow-Origin"] = "*";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// The port, in a file named by this process id - a different name from the other two links, on purpose.
    /// </summary>
    /// <remarks>
    /// A client globs for all three and knows what it has found by the name: a canvas, a Rhino somebody is
    /// looking at, or one nobody is. Sharing a name would make them indistinguishable, and they answer
    /// different questions.
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
            // Without the file a client has to be told the port, which is worse but not fatal.
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
            // A stale file is caught by the pid check on the client side; best effort is enough.
        }
    }
}
