using System.Net;
using System.Text;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The loopback interface to Rhino itself: what the process is doing, and how to answer what is blocking it.
/// </summary>
/// <remarks>
/// Same conventions as the canvas link next door - plain HTTP on 127.0.0.1, one JSON out, an ephemeral port
/// written to a discovery file - and deliberately a different file, because these are different things. The
/// canvas link answers about a document; this answers about a process, and it answers when the document
/// link cannot, which is the entire reason it exists.
/// <para>
/// Nothing here runs on the Rhino UI thread. That is not an optimisation - it is the requirement. Every
/// verb has to work while that thread is held, because a thread that is held is what all of this is for.
/// </para>
/// </remarks>
internal static class RhinoServer
{
    private static HttpListener? listener;

    internal static int Port { get; private set; }

    private const string Description = """
        {
          "phenome": "rhino-link",
          "version": "0.1",
          "protocol": {
            "GET /": "this description",
            "GET /pulse": "whether Rhino is idle, busy or blocked. Answered off the UI thread, so it answers when nothing else does. 'busy' names the running command and how long it has run: wait. 'blocked' names the open dialog and lists its buttons: nothing will answer until it is clicked",
            "POST /dismiss": "SUPERSEDED by /dialog, and kept working. {button?, key?, expect?} - press a button by name, type a key, or close it when neither is given. When /pulse says clickable:false the dialog draws its own buttons and only a key reaches it",
            "POST /dialog": "{button?, key?, close?, expect?} - answer the open dialog. Nothing is assumed: with no answer given this refuses and lists the buttons, because a decline by omission cannot be told from a decline by decision. 'close' declines, said out loud. No verb here guesses which button means yes - on a save prompt the affirmative is whichever of Save and Don't Save you meant",
            "POST /escape": "{times?} - post Escape to Rhino, cancelling whatever it is waiting for. For the case /dismiss cannot answer: a command waiting on a pick is not a dialog, so nothing is disabled and there is no window to click, yet the UI thread is held and every other verb reports 'busy' as though waiting would help. Scripting an interactive command is the ordinary way to get here. 'times' cancels that many levels; one by default",
            "POST /command": "{script} - run a Rhino command script. Here rather than only on the canvas link, because Rhino is what runs commands and Grasshopper need not be open for it",
            "GET /doc": "the Rhino document: name, layers, object count",
            "GET /console": "?tail=50 - the tail of Rhino's command line, which is where Rhino answers. One capture per Rhino and this is it; the canvas link reads from here",
            "GET /plugins": "?all=false - every plug-in Rhino has a record of, with the runtime it would load into: loaded, dotnet, loadProtected, the path Rhino believes and the registry key. This is how to answer 'why is my plug-in not loading' without proving the registry innocent by hand. Shipped plug-ins are left out unless all=true, because there are a hundred of them and they are never the suspect",
            "POST /load": "{id?, path?} - load a plug-in on purpose, quietly and again even if a previous attempt failed. Rhino remembers a failure and will not retry, which is why the ordinary build-and-load loop appears to do nothing the second time round. Answers with what Rhino's record says afterwards rather than with a result word meaning 'no'",
            "GET /screenshot": "?width=640&zoomExtents=true - the active viewport as PNG (base64), framed on the geometry for the capture and the camera put back where the human left it",
            "GET /camera": "where the active viewport is looking: projection, location, target, up, 35mm lens length and the viewport's pixel size",
            "POST /camera": "{location?, target?, up?, lens?, projection?} - aim the active viewport; only what you pass changes. This is how to frame a view deliberately: Rhino's Zoom is interactive and a scripted one waits for a pick that never comes, which holds the UI thread and takes every other verb down with it"
          },
          "why": "Grasshopper's link only exists once Grasshopper has been started, so it cannot report on anything that happens before that - including a dialog on startup, which is exactly when nothing else can answer.",
          "discovery": "%TEMP%/phenome-rhino-<rhino pid>.port holds this port"
        }
        """;

    internal static void Start()
    {
        Pulse.Start();
        CommandLine.Start();

        // Bound before the port is named, and named only once bound: two Rhinos starting together used to be
        // able to race for the same ephemeral port here, and the loser wrote a discovery file for a port
        // nothing was listening on.
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
                    Answer(context);
                }
                catch (Exception) when (listener is null || !listener.IsListening)
                {
                    // Shut down mid-await; not an incident.
                }
                catch (Exception)
                {
                    // Nowhere useful to say it: writing to the command line needs the UI thread, and this
                    // server exists for the times that thread is not available.
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

    private static void Answer(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath.TrimEnd('/') ?? "";
        string method = context.Request.HttpMethod;
        string payload = method == "POST" ? ReadBody(context.Request) : "";

        try
        {
            string body = (method, path) switch
            {
                ("GET", "") => Description,
                ("GET", "/pulse") => Pulse.Report(),
                ("POST", "/dismiss") => Dismissed(payload),
                ("POST", "/dialog") => AnswerDialog(payload),
                ("POST", "/escape") => Escaped(payload),
                ("POST", "/command") => Commands.Run(payload),
                ("GET", "/doc") => Commands.Document(),
                ("GET", "/plugins") => Plugins.List(
                    string.Equals(context.Request.QueryString["all"], "true", StringComparison.OrdinalIgnoreCase)),
                ("POST", "/load") => Plugins.Load(payload),
                ("GET", "/screenshot") => View.Screenshot(context.Request),
                ("GET", "/camera") => View.ReadCamera(),
                ("POST", "/camera") => View.AimCamera(payload),
                ("GET", "/console") => CommandLine.Tail(
                    int.TryParse(context.Request.QueryString["tail"], out int back) ? Math.Clamp(back, 1, 500) : 50),
                _ => throw new KeyNotFoundException($"There is no {method} {path}. GET / describes what there is."),
            };

            Respond(context.Response, 200, body);
        }
        catch (KeyNotFoundException missing)
        {
            Respond(context.Response, 404, $"{{\"ok\":false,\"error\":{Json.Quote(missing.Message)}}}");
        }
        catch (Exception failure)
        {
            Respond(context.Response, 500, $"{{\"ok\":false,\"error\":{Json.Quote(failure.Message)}}}");
        }
    }

    private static string Dismissed(string payload)
    {
        string? button = null;
        string? expect = null;
        string? key = null;

        if (!string.IsNullOrWhiteSpace(payload))
        {
            using JsonDocument request = JsonDocument.Parse(payload);
            button = Json.Text(request, "button");
            expect = Json.Text(request, "expect");

            // Was missing here while the protocol text above and the MCP schema both promised it, so a
            // caller's 'key' was read and thrown away. Rhino 8's own dialogs have no clickable buttons,
            // which makes a key the only way into exactly the dialogs this half exists to answer.
            key = Json.Text(request, "key");
        }

        return Pulse.Dismiss(button, expect, key);
    }

    /// <summary>Answers the open dialog, with nothing assumed when nothing was said.</summary>
    private static string AnswerDialog(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Pulse.Answer(null, null, close: false, null);
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        return Pulse.Answer(
            Json.Text(request, "button"),
            Json.Text(request, "key"),
            request.RootElement.TryGetProperty("close", out JsonElement asked)
                && asked.ValueKind == JsonValueKind.True,
            Json.Text(request, "expect"));
    }

    private static string Escaped(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return Pulse.Escape(1);
        }

        using JsonDocument request = JsonDocument.Parse(payload);

        return Pulse.Escape(Json.Int(request, "times", 1));
    }

    private static string ReadBody(HttpListenerRequest request)
    {
        using StreamReader reader = new(request.InputStream, request.ContentEncoding);
        return reader.ReadToEnd();
    }

    private static void Respond(HttpListenerResponse response, int status, string body)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);

        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes, 0, bytes.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// The port, in a file named by Rhino's process id - so a client finds this Rhino rather than a Rhino.
    /// </summary>
    private static string PortFile =>
        Path.Combine(Path.GetTempPath(), $"phenome-rhino-{Environment.ProcessId}.port");

    private static void WritePortFile()
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

    private static void DeletePortFile()
    {
        try
        {
            if (File.Exists(PortFile)) File.Delete(PortFile);
        }
        catch (Exception)
        {
            // A stale port file answers nothing and is replaced on the next start.
        }
    }
}
