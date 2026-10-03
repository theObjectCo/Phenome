using System.Text;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Captures the tail of Rhino's command line so an agent can read what Rhino printed.
/// </summary>
/// <remarks>
/// Rhino reports only on its command line: selection counts, exporter option lists, and warnings appear there
/// and nowhere else. The capture makes them readable by an agent as well as by a person.
/// <para>
/// There is one drain per Rhino. <c>CapturedCommandWindowStrings</c> clears the buffer as it reads, and two
/// readers would lose each other's lines. This plugin loads with Rhino, before any canvas exists, and owns the
/// capture. The canvas link detects that capture is already on and reads from here instead of starting a
/// second one.
/// </para>
/// <para>
/// The drain runs on the UI thread and cannot read the command line while that thread is blocked. A long
/// script's output arrives when the script ends. Use Pulse to check state in the meantime.
/// </para>
/// </remarks>
internal static class CommandLine
{
    /// <summary>Holds enough lines for a command's output at little cost. Oldest lines are dropped first.</summary>
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
                // Rhino can emit partial lines (a prompt, then its answer), and an entry is not always one
                // full line. Blank entries are the newlines between them.
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
            "Drained when the UI thread is next free. A long command's output arrives when it ends. Ask /pulse for the current state."));
        json.Append('}');

        return json.ToString();
    }
}
