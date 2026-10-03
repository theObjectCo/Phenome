using System.Reflection;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// Records refused requests and the notes agents leave about the gap between what they expected and got.
/// </summary>
/// <remarks>
/// The journal keeps what happened; this keeps what failed, the half that improves the bridge. Without it a
/// refusal reaches the agent as one message and is gone, and a fault report has to be rebuilt from memory.
/// <para>
/// The log is strictly local: one JSONL file under the user's application data, never sent, with no telemetry
/// of any kind. Sharing it is a deliberate act: the user hands the file over or asks an agent to summarise it.
/// Entries carry the plugin version, and a report can be traced to a build.
/// </para>
/// </remarks>
internal static class Friction
{
    private const long TooBig = 2_000_000;
    private const int PayloadKept = 600;

    private static readonly object Gate = new();

    /// <summary>
    /// Cross-process lock over the log file.
    /// </summary>
    /// <remarks>
    /// Several Rhinos write one file. The in-process lock alone lets two of them append to the same path and
    /// run the same read-halve-rewrite over each other. A log that cannot be written stays silent, and entries
    /// would go missing unnoticed. A named mutex is the smallest lock that covers the file across processes.
    /// <para>
    /// The mutex is Local, not Global. The log lives under the user's application data and the relevant scope
    /// is the session; Global would need privileges this should not ask for.
    /// </para>
    /// </remarks>
    private static readonly Mutex Across = new(initiallyOwned: false, "Local\\PhenomeLinkFriction");

    /// <summary>
    /// Runs <paramref name="work"/> with both locks held, or without the shared one if it cannot be acquired.
    /// </summary>
    /// <remarks>
    /// After two seconds without the mutex the work runs anyway. A log entry is worth writing even while
    /// another process holds the mutex, and blocking a request on a log is worse than the interleaving it
    /// would prevent.
    /// </remarks>
    private static T Guarded<T>(Func<T> work)
    {
        lock (Gate)
        {
            bool held = false;

            try
            {
                try
                {
                    held = Across.WaitOne(TimeSpan.FromSeconds(2));
                }
                catch (AbandonedMutexException)
                {
                    // The previous holder died while holding it. This process now holds the mutex, and the
                    // file may be half rewritten; the trim below tolerates that because it only drops old lines.
                    held = true;
                }

                return work();
            }
            finally
            {
                if (held)
                {
                    try
                    {
                        Across.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // This thread does not own the mutex; there is nothing to undo.
                    }
                }
            }
        }
    }

    // Strips the build metadata a git hash appends; a subject line carries the version only.
    private static readonly string Version = (typeof(Friction).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0").Split('+')[0];

    /// <summary>Where the log lives. The path is logged at load.</summary>
    internal static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Phenome",
        "link-friction.jsonl");

    /// <summary>A request the server refused, with what was asked and what it said back.</summary>
    internal static void Refused(string verb, string payload, string said) =>
        Append($"{{\"kind\":\"refused\",\"verb\":{Json.Quote(verb)},"
            + $"\"asked\":{Json.Quote(Trim(payload))},\"said\":{Json.Quote(said)}}}");

    /// <summary>An agent's own words: what it expected, what it got, and anything else worth saying.</summary>
    internal static void Reported(string author, string expected, string got, string? notes)
    {
        Append($"{{\"kind\":\"report\",\"author\":{Json.Quote(author)},"
            + $"\"expected\":{Json.Quote(expected)},\"got\":{Json.Quote(got)}"
            + (notes is null ? "" : $",\"notes\":{Json.Quote(notes)}")
            + "}");
    }

    /// <summary>The last few entries, for assembling a report.</summary>
    internal static string Tail(int lines)
    {
        return Guarded(() =>
        {
            if (!File.Exists(Path))
            {
                return "{\"path\":" + Json.Quote(Path) + ",\"entries\":[]}";
            }

            string[] all = ReadLines();
            IEnumerable<string> kept = all.Length > lines ? all[^lines..] : all;

            return "{\"path\":" + Json.Quote(Path) + ",\"entries\":[" + string.Join(",", kept) + "]}";
        });
    }

    /// <summary>
    /// The log's lines, tolerating another process holding the file open.
    /// </summary>
    /// <remarks>
    /// <c>File.ReadAllLines</c> requests exclusive read and throws when a second Rhino is mid-append; the
    /// instance that reads second gets an error. Opening with explicit share flags removes that failure.
    /// </remarks>
    private static string[] ReadLines()
    {
        using FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);

        List<string> lines = [];

        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return [.. lines];
    }

    /// <summary>Default report recipient when nothing else is given: a role address, not a person.</summary>
    internal const string Intake = "hi+phenomelogs@object.pl";

    /// <summary>
    /// The whole complaint as one file a person can read and send: what was expected, what happened, the
    /// session it happened in, and the friction log behind it.
    /// </summary>
    /// <remarks>
    /// The report is assembled, saved and returned, and never sent. The user sends it from their own mail
    /// client after reading it; an agent only asks whether to prepare it.
    /// </remarks>
    internal static (string Path, string Subject, string Body, string Mailto) Draft(
        string expected,
        string got,
        string session,
        string findings,
        string? to)
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string file = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Path)!,
            $"phenome-link-report-{stamp}.md");

        string report = $"""
            # Phenome Link report

            - **When:** {DateTime.Now:yyyy-MM-dd HH:mm}
            - **Plugin:** Phenome Link {Version}
            - **Rhino:** {Rhino.RhinoApp.Version}

            ## Expected

            {expected}

            ## Got

            {got}

            ## Session

            {session}

            ## Composition review

            ```json
            {findings}
            ```

            ## Friction log (recent)

            ```jsonl
            {RecentLines(80)}
            ```

            ---
            Assembled locally by Phenome Link. Nothing here was sent anywhere. This file is the whole
            report, and it goes only where its reader sends it.
            """;

        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                File.WriteAllText(file, report);
            }
            catch (Exception failure)
            {
                throw new InvalidOperationException($"Could not write the report: {failure.Message}");
            }
        }

        string subject = $"Phenome Link {Version}: {Shorten(expected)}";
        string body = $"{expected}\r\n\r\nGot instead:\r\n{got}\r\n\r\nThe full report, with the session "
            + $"and the friction log, is at:\r\n{file}\r\n\r\n(Attach that file before sending: mail "
            + "bodies are too small for the log itself.)\r\n";

        // A mailto link opens the mail client prefilled, and nothing leaves the machine automatically: the
        // person reads the message, attaches the file and sends it.
        string mailto = $"mailto:{Uri.EscapeDataString(to ?? Intake)}"
            + $"?subject={Uri.EscapeDataString(subject)}&body={Uri.EscapeDataString(body)}";

        return (file, subject, body, mailto);
    }

    private static string RecentLines(int lines)
    {
        try
        {
            if (!File.Exists(Path))
            {
                return "(nothing logged)";
            }

            string[] all = ReadLines();

            return string.Join(Environment.NewLine, all.Length > lines ? all[^lines..] : all);
        }
        catch (Exception)
        {
            return "(the log could not be read)";
        }
    }

    private static string Shorten(string line)
    {
        string one = line.ReplaceLineEndings(" ").Trim();

        return one.Length <= 70 ? one : one[..70] + "…";
    }

    private static void Append(string entry)
    {
        Guarded<bool>(() =>
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);

                // Past 2 MB the file is halved: the oldest entries are the least useful, and clearing the
                // file at a threshold would discard a session mid-report.
                if (File.Exists(Path) && new FileInfo(Path).Length > TooBig)
                {
                    string[] all = ReadLines();

                    File.WriteAllLines(Path, all[(all.Length / 2)..]);
                }

                File.AppendAllText(
                    Path,
                    $"{{\"at\":\"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\",\"link\":{Json.Quote(Version)},"
                        + entry[1..] + Environment.NewLine);
            }
            catch (Exception)
            {
                // A failed write is ignored, and the request that triggered it carries on.
            }

            return true;
        });
    }

    private static string Trim(string payload) =>
        payload.Length <= PayloadKept ? payload : payload[..PayloadKept] + "…";
}
