using System.Reflection;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Bridge;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>What Grasshopper shows about this library.</summary>
public class LinkLibrary : GH_AssemblyInfo
{
    /// <inheritdoc/>
    public override string Name => "Phenome Link";

    /// <inheritdoc/>
    public override string Description =>
        "A loopback interface to the canvas, for agents: the document as JSON, a journal of what happens " +
        "on it, and verbs to act. Any client that can make an HTTP request is a peer.";

    /// <inheritdoc/>
    public override Guid Id => new("b7d3a91c-2e58-4f06-8a4d-91c5e7b30f62");

    /// <inheritdoc/>
    public override string AuthorName => "Phenome";

    /// <inheritdoc/>
    public override string AuthorContact => string.Empty;

    /// <inheritdoc/>
    public override string AssemblyVersion => typeof(LinkLibrary).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.1.0";
}

/// <summary>Where it says things: the command line, and the same log file the components plugin writes.</summary>
internal static class LinkLog
{
    internal static void Say(string line)
    {
        Rhino.RhinoApp.WriteLine(line);

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "phenome-grasshopper.log"),
                $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // The log is a courtesy; failing to write it must not become its own incident.
        }
    }
}

/// <summary>
/// Starts the bridge as the plugin loads: the server, the watcher, the discovery file.
/// </summary>
/// <remarks>
/// The discovery file is the protocol's only fixed point: <c>%TEMP%\phenome-link-&lt;pid&gt;.port</c> holds
/// the port, one file per Rhino, deleted when Rhino closes. A client globs for the files, checks the pids
/// are alive, and knows every session on the machine - no configuration, no collisions.
/// </remarks>
public class LinkRegistration : GH_AssemblyPriority
{
    /// <inheritdoc/>
    public override GH_LoadingInstruction PriorityLoad()
    {
        try
        {
            LinkServer.Start();
            DocumentWatcher.Start();

            string discovery = Path.Combine(
                Path.GetTempPath(),
                $"phenome-link-{Environment.ProcessId}.port");

            File.WriteAllText(discovery, LinkServer.Port.ToString());

            SweepStaleFiles();

            Rhino.RhinoApp.Closing += (_, _) =>
            {
                try
                {
                    File.Delete(discovery);
                }
                catch (Exception)
                {
                    // A stale file is caught by the pid check on the client side; best effort is enough.
                }
            };

            // The invitation rides on every canvas, and puts itself away once someone is paired.
            global::Grasshopper.GUI.Canvas.GH_Canvas.WidgetListCreated += (_, gathering) =>
                gathering.AddWidget(new PairWidget());

            LinkLog.Say($"Phenome Link: listening on http://127.0.0.1:{LinkServer.Port}/ ({discovery}).");
            LinkLog.Say($"Phenome Link: friction log at {Friction.Path} - local only, share it if you want the bridge fixed.");
        }
        catch (Exception failure)
        {
            // Said out loud and out of the way: a bridge that fails to open must not take Grasshopper down.
            LinkLog.Say($"Phenome Link: could not start. {failure}");
        }

        return GH_LoadingInstruction.Proceed;
    }

    /// <summary>
    /// Deletes discovery files and autosaves belonging to Rhinos that are gone.
    /// </summary>
    /// <remarks>
    /// The Closing handler above removes this session's port file, and that covers the ordinary exit.
    /// It does not cover a kill -- and anything driving Rhino from outside kills it sooner or later,
    /// because installing a plugin means closing a Rhino that is holding the assembly. Left alone the
    /// files accumulate: 28 of them had gathered here, 27 for processes that no longer existed, along
    /// with fifty autosaves. The client side survives it by checking each pid, so nothing was broken;
    /// it was simply litter that nobody had made anybody's job.
    /// <para>
    /// Sweeping on start rather than on exit is the whole point: exit is precisely the moment that does
    /// not always happen. Every fault is swallowed -- a link that will not start because it could not
    /// delete somebody else's leftover file would be a far worse trade.
    /// </para>
    /// </remarks>
    private static void SweepStaleFiles()
    {
        try
        {
            string temp = Path.GetTempPath();

            foreach (string file in Directory.EnumerateFiles(temp, "phenome-*-*.port"))
            {
                string tail = Path.GetFileNameWithoutExtension(file).Split('-').LastOrDefault() ?? "";

                if (!int.TryParse(tail, out int owner) || owner == Environment.ProcessId)
                {
                    continue;
                }

                try
                {
                    // Still running means still valid, whichever plugin wrote it.
                    using (System.Diagnostics.Process.GetProcessById(owner))
                    {
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                    // No such process: the file outlived its Rhino.
                }

                try
                {
                    File.Delete(file);
                }
                catch (Exception)
                {
                    // Another session may be sweeping the same file; whoever wins, it goes.
                }
            }

            // Autosaves are named by document, not by process, so there is no pid to test. Age is the
            // only honest signal, and a week is long enough that anything still wanted has been noticed.
            DateTime cutoff = DateTime.Now.AddDays(-7);

            foreach (string file in Directory.EnumerateFiles(temp, "phenome-autosave-*.gh"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch (Exception)
                {
                    // As above.
                }
            }
        }
        catch (Exception failure)
        {
            LinkLog.Say($"Phenome Link: could not sweep stale files ({failure.Message}); carrying on.");
        }
    }
}
