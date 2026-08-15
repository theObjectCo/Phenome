using System.Net;
using System.Net.Sockets;
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
            "POST /dismiss": "{button?, expect?} - answer the open dialog: press a button by name, or close it when no name is given. 'expect' names the dialog you meant to answer and refuses if another is up by then"
          },
          "why": "Grasshopper's link only exists once Grasshopper has been started, so it cannot report on anything that happens before that - including a dialog on startup, which is exactly when nothing else can answer.",
          "discovery": "%TEMP%/phenome-rhino-<rhino pid>.port holds this port"
        }
        """;

    internal static void Start()
    {
        Pulse.Start();

        Port = FreePort();

        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        listener.Start();

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

        if (!string.IsNullOrWhiteSpace(payload))
        {
            using JsonDocument request = JsonDocument.Parse(payload);
            button = Text(request, "button");
            expect = Text(request, "expect");
        }

        return Pulse.Dismiss(button, expect);
    }

    private static string? Text(JsonDocument request, string name) =>
        request.RootElement.TryGetProperty(name, out JsonElement field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

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

    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
