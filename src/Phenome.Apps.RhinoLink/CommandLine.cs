using System.Text;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The tail of Rhino's command line, kept so an agent can read what Rhino said.
/// </summary>
/// <remarks>
/// Rhino answers on its command line and nowhere else: "56 curves added to selection" is the answer to a
/// selection, an exporter's option list is the answer to an export, and a warning is the reason a command
/// did something surprising. Capture is what turns that from something a human reads into something an
/// agent can.
/// <para>
/// There is exactly one drain per Rhino, because <c>CapturedCommandWindowStrings</c> clears the buffer as
/// it reads and two readers would steal each other's lines. This plugin loads with Rhino, before any
/// canvas exists, so this is the one - the canvas link checks whether capture is already on and, finding
/// it is, asks here instead of starting a second.
/// </para>
/// <para>
/// What this cannot do: show the command line <em>while</em> the UI thread is blocked, because the drain
/// runs on that thread. A long script's output arrives in one piece when the script ends. Pulse is the
/// verb for the meantime - it says whether there will be an end.
/// </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>Long enough to hold what a command said, short enough to stay cheap. Oldest lines fall off.</summary>
    private const int Kept = 500;

    private static readonly Queue<string> lines = new();
    private static readonly object gate = new();

    private static long dropped;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            Rhino.RhinoApp.CommandWindowCaptureEnabled = true;
            Rhino.RhinoApp.Idle += (_, _) => Drain();
        });
    }

    private static bool IsOurs(string line) =>
        line.StartsWith("Phenome Link:", StringComparison.Ordinal);

    private static void Drain()
    {
        string[]? captured;

        try
        {
            captured = Rhino.RhinoApp.CapturedCommandWindowStrings(clearBuffer: true);
        }
        catch (Exception)
        {
            return;
        }

        if (captured is null || captured.Length == 0)
        {
            return;
        }

        lock (gate)
        {
            foreach (string raw in captured)
            {
                // Rhino writes partial lines too - a prompt, then its answer - so what arrives is not
                // always one line per entry. Blank entries are the newlines between them.
                string line = raw.TrimEnd('\r', '\n');

                if (line.Length == 0 || IsOurs(line))
                {
                    continue;
                }

                lines.Enqueue(line);

                while (lines.Count > Kept)
                {
                    lines.Dequeue();
                    dropped++;
                }
            }
        }
    }

    /// <summary>The last <paramref name="tail"/> lines, newest last.</summary>
    internal static string Tail(int tail)
    {
        string[] recent;
        long lost;

        lock (gate)
        {
            recent = lines.Skip(Math.Max(0, lines.Count - tail)).ToArray();
            lost = dropped;
        }

        StringBuilder json = new();

        json.Append("{\"ok\":true,\"lines\":[");

        for (int i = 0; i < recent.Length; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(Json.Quote(recent[i]));
        }

        json.Append(']');
        json.Append(",\"kept\":").Append(Json.Number(recent.Length));
        json.Append(",\"dropped\":").Append(Json.Number(lost));
        json.Append(",\"note\":").Append(Json.Quote(
            "Drained when the UI thread breathes, so a long command's output arrives when it ends. Ask /pulse for what is happening now."));
        json.Append('}');

        return json.ToString();
    }
}
