using Rhino.PlugIns;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Loads with Rhino and starts answering about the process before any canvas exists.
/// </summary>
/// <remarks>
/// Loads at startup. The case this covers is a dialog that appears while Rhino is still starting. A plugin
/// loaded on first request could not load then, because the request that would trigger it is itself blocked
/// by that dialog.
/// </remarks>
public class RhinoLinkPlugIn : PlugIn
{
    /// <summary>Rhino constructs this, and the instance is stored for other code to find.</summary>
    public RhinoLinkPlugIn()
    {
        Instance = this;
    }

    /// <summary>The one instance Rhino made, or null before it has.</summary>
    public static RhinoLinkPlugIn? Instance { get; private set; }

    /// <summary>At startup, because the events it reports happen before anything could request them.</summary>
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    /// <summary>Starts the loopback server, or reports why it could not.</summary>
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            RhinoServer.Start();

            // This half fetches its advisory on its own and does not wait for the other. The two are separate
            // assemblies, each with its own copy of the shared source and its own state, and neither can
            // assume the other loaded: a Rhino started without Grasshopper has this half only. The cost is two
            // small requests per start.
            Advisory.Watch(notice =>
                Rhino.RhinoApp.WriteLine(
                    notice.Sentence + " The Rhino link is refusing verbs until it is updated."));
        }
        catch (Exception failure)
        {
            // A diagnostic channel that fails to load is worse than none. Rhino would report a broken plugin,
            // which adds a second problem and solves nothing.
            errorMessage = $"Phenome Rhino Link did not start: {failure.Message}";
            return LoadReturnCode.ErrorShowDialog;
        }

        return LoadReturnCode.Success;
    }

    /// <summary>Stops listening and deletes the discovery file.</summary>
    protected override void OnShutdown()
    {
        RhinoServer.Stop();
    }
}
