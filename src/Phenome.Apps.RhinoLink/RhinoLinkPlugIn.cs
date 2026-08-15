using Rhino.PlugIns;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Loads with Rhino and starts answering about the process, before any canvas exists.
/// </summary>
/// <remarks>
/// At startup rather than on demand, deliberately: the situation this exists for is a dialog that appears
/// while Rhino is still starting, and a plugin that loads when first asked for would be asked for by a
/// request that cannot arrive, because the thing blocking it is the dialog.
/// </remarks>
public class RhinoLinkPlugIn : PlugIn
{
    /// <summary>Rhino constructs this itself; the instance is kept so anything else can find it.</summary>
    public RhinoLinkPlugIn()
    {
        Instance = this;
    }

    /// <summary>The one instance Rhino made, or null before it has.</summary>
    public static RhinoLinkPlugIn? Instance { get; private set; }

    /// <summary>At startup, because what this reports on happens before anything would ask for it.</summary>
    public override PlugInLoadTime LoadTime => PlugInLoadTime.AtStartup;

    /// <summary>Starts the loopback server, or says why it could not.</summary>
    protected override LoadReturnCode OnLoad(ref string errorMessage)
    {
        try
        {
            RhinoServer.Start();
        }
        catch (Exception failure)
        {
            // A diagnostic channel that refuses to load is worse than none: Rhino would report a broken
            // plugin and the human would have a second problem instead of a first one solved.
            errorMessage = $"Phenome Rhino Link did not start: {failure.Message}";
            return LoadReturnCode.ErrorShowDialog;
        }

        return LoadReturnCode.Success;
    }

    /// <summary>Stops listening and takes the discovery file away with it.</summary>
    protected override void OnShutdown()
    {
        RhinoServer.Stop();
    }
}
