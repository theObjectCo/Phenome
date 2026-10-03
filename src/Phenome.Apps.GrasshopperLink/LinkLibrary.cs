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

/// <summary>Writes to the command line and to the same log file the components plugin writes.</summary>
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
            // Writing the log is best effort: a write failure must not become an error of its own.
        }
    }
}

/// <summary>
/// Starts the bridge as the plugin loads: the server, the document watcher and the discovery file.
/// </summary>
/// <remarks>
/// The discovery file is the protocol's only fixed point. <c>%TEMP%\phenome-link-&lt;pid&gt;.port</c> holds
/// the port; there is one file per Rhino, deleted when Rhino closes. A client globs the files, checks that each
/// pid is alive and finds every session on the machine without configuration or collisions.
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
                    // Deleting is best effort: a leftover file is caught by the client-side pid check.
                }
            };

            // Adds the pairing button to every canvas; it hides itself once an agent is paired.
            global::Grasshopper.GUI.Canvas.GH_Canvas.WidgetListCreated += (_, gathering) =>
                gathering.AddWidget(new PairWidget());

            // Checks once, in the background, whether this version has been withdrawn, and reports it if so.
            // The link is already serving when the answer arrives, because blocking Grasshopper's first draw on
            // the network would be worse than the problem. Nothing is sent: the notice is a static file, and the
            // comparison happens here. PHENOME_IGNORE_ADVISORY=1 disables the check.
            Advisory.Watch(notice =>
            {
                LinkLog.Say(notice.Sentence);
                LinkLog.Say("Phenome Link: the canvas link is refusing verbs until it is updated.");
            });

            if (Advisory.Overridden)
            {
                LinkLog.Say(
                    "Phenome Link: PHENOME_IGNORE_ADVISORY is set - safety notices are being ignored on "
                    + "this machine.");
            }

            LinkLog.Say($"Phenome Link: listening on http://127.0.0.1:{LinkServer.Port}/ ({discovery}).");
            LinkLog.Say($"Phenome Link: friction log at {Friction.Path} - local only. Sharing it helps get the bridge fixed.");
        }
        catch (Exception failure)
        {
            // Report and continue: a bridge that fails to open must not take Grasshopper down.
            LinkLog.Say($"Phenome Link: could not start. {failure}");
        }

        return GH_LoadingInstruction.Proceed;
    }

    /// <summary>
    /// Deletes discovery files and autosaves from Rhinos that have exited.
    /// </summary>
    /// <remarks>
    /// The Closing handler above removes this session's port file on a normal exit, and a killed Rhino leaves
    /// its file behind. An external driver kills Rhino sooner or later, because installing a plugin means closing
    /// a Rhino that holds the assembly. Orphaned files accumulate (27 dead port files and about 50 autosaves in
    /// one observed case). The client-side pid check tolerates them, but they are still litter.
    /// <para>
    /// The sweep runs on start because exit is the step that may not happen. Every fault is swallowed: refusing
    /// to start because a leftover file could not be deleted would be worse.
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
                    // A live pid means the file is still valid, whichever plugin wrote it.
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
                    // Another session may be deleting the same file, and either deletion removes it.
                }
            }

            // Autosaves are named by document and carry no pid to test. Age is the only signal, and a week is
            // long enough for anything still wanted to have been noticed.
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
                    // Best effort, as for the port files above.
                }
            }
        }
        catch (Exception failure)
        {
            LinkLog.Say($"Phenome Link: could not sweep stale files ({failure.Message}); carrying on.");
        }
    }
}
