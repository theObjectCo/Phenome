using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Bridge;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Writes the canvas side of the conversation to the journal, one entry per new text.
/// </summary>
/// <remarks>
/// Sends when Send is true and the text is new. Send is meant for a momentary button that springs back, and a
/// stale true across recomputes must not resend the same text. The agent reads the message from the journal,
/// which is the only delivery.
/// </remarks>
public class SendMessageComponent : GH_Component
{
    private string? lastSent;

    /// <summary>Component that writes a message to the journal.</summary>
    public SendMessageComponent()
        : base(
            "Send to Agent",
            "Say",
            "Sends a message into the link's journal, where a paired agent reads it. " +
            "Wire a button to Send; the message goes out once per new text.",
            "Phenome",
            "Link")
    {
    }

    /// <inheritdoc/>
    public override Guid ComponentGuid => new("4a8e2f61-9c05-4b7a-8d3e-6f1b0c92e754");

    /// <inheritdoc/>
    public override GH_Exposure Exposure => GH_Exposure.primary;

    /// <inheritdoc/>
    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
        pManager.AddTextParameter("Text", "T", "What to say.", GH_ParamAccess.item);
        pManager.AddTextParameter(
            "To",
            "@",
            "Recipient. Empty broadcasts to all connected clients.",
            GH_ParamAccess.item,
            "");
        pManager.AddBooleanParameter("Send", "S", "True sends. Wire a button here.", GH_ParamAccess.item, false);
    }

    /// <inheritdoc/>
    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Sent", "S", "What went out last.", GH_ParamAccess.item);
    }

    /// <inheritdoc/>
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        string text = "";
        string to = "";
        bool send = false;

        DA.GetData(0, ref text);
        DA.GetData(1, ref to);
        DA.GetData(2, ref send);

        if (send && !string.IsNullOrWhiteSpace(text) && text != lastSent)
        {
            Journal.AppendMessage("human", text, string.IsNullOrWhiteSpace(to) ? null : to);
            lastSent = text;
        }

        DA.SetData(0, lastSent ?? "");
    }
}

/// <summary>
/// Shows the other half of the conversation: agent messages as a live list.
/// </summary>
/// <remarks>
/// Expires its solution when a message is appended to the journal, and replies appear with no manual recompute.
/// </remarks>
public class AgentRepliesComponent : GH_Component
{
    /// <summary>Component that shows the conversation.</summary>
    public AgentRepliesComponent()
        : base(
            "Agent Replies",
            "Replies",
            "The messages agents sent over the link, newest last. Wire a panel to read the conversation.",
            "Phenome",
            "Link")
    {
        Journal.Appended += OnJournal;
    }

    /// <inheritdoc/>
    public override Guid ComponentGuid => new("d92c5b38-1e74-4f0a-9b86-3c5a7e40d216");

    /// <inheritdoc/>
    public override GH_Exposure Exposure => GH_Exposure.primary;

    /// <inheritdoc/>
    public override void RemovedFromDocument(GH_Document document)
    {
        Journal.Appended -= OnJournal;
        base.RemovedFromDocument(document);
    }

    private void OnJournal(string kind)
    {
        if (kind != "message")
        {
            return;
        }

        // The journal fires on whichever thread wrote the entry; expiring a solution belongs to the UI thread.
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            if (OnPingDocument() is not null)
            {
                ExpireSolution(true);
            }
        });
    }

    /// <inheritdoc/>
    protected override void RegisterInputParams(GH_InputParamManager pManager)
    {
    }

    /// <inheritdoc/>
    protected override void RegisterOutputParams(GH_OutputParamManager pManager)
    {
        pManager.AddTextParameter("Messages", "M", "The conversation, oldest first.", GH_ParamAccess.list);
    }

    /// <inheritdoc/>
    protected override void SolveInstance(IGH_DataAccess DA)
    {
        List<string> lines = [];

        foreach ((string author, string text, string? to) in Journal.RecentMessages())
        {
            lines.Add(to is null ? $"{author}: {text}" : $"{author} → {to}: {text}");
        }

        DA.SetDataList(0, lines);
    }
}
