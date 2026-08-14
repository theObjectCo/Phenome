using System.Text;

using Grasshopper.Kernel;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Turns what happens on the canvas into journal entries.
/// </summary>
/// <remarks>
/// Grasshopper's own document events are the source: objects added and deleted, documents opened and
/// closed, solutions ending - the last with a summary of what went red, so an agent knows the canvas is
/// unhappy without asking for the whole state. Entries authored here say <c>canvas</c>: they describe what
/// the document did, whoever's hand caused it; a client that wants to know whose hand reads the verbs
/// journalled by the server alongside.
/// </remarks>
internal static class DocumentWatcher
{
    internal static void Start()
    {
        GH_DocumentServer documents = global::Grasshopper.Instances.DocumentServer;

        documents.DocumentAdded += (_, document) =>
        {
            Hook(document);
            Journal.Append("canvas", "documentOpened", $",\"name\":{Json.Quote(document.DisplayName ?? "unsaved")}");
        };

        documents.DocumentRemoved += (_, document) =>
            Journal.Append("canvas", "documentClosed", $",\"name\":{Json.Quote(document.DisplayName ?? "unsaved")}");

        foreach (GH_Document document in documents)
        {
            Hook(document);
        }
    }

    private static void Hook(GH_Document document)
    {
        document.ObjectsAdded += (_, added) =>
            Journal.Append("canvas", "objectsAdded", Named(added.Objects));

        document.ObjectsDeleted += (_, deleted) =>
            Journal.Append("canvas", "objectsDeleted", Named(deleted.Objects));

        document.SolutionEnd += (_, _) =>
            Journal.Append("canvas", "solutionEnd", Complaints(document));
    }

    private static string Named(IEnumerable<IGH_DocumentObject> objects)
    {
        StringBuilder json = new(",\"objects\":[");
        bool first = true;

        foreach (IGH_DocumentObject thing in objects)
        {
            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"id\":").Append(Json.Quote(thing.InstanceGuid.ToString()));
            json.Append(",\"name\":").Append(Json.Quote(thing.Name)).Append('}');
        }

        return json.Append(']').ToString();
    }

    /// <summary>
    /// What went red or orange, and what took the time - the entry says not just that a solve ended but
    /// how it went and where it was spent, so "why is this slow" is answered from the journal.
    /// </summary>
    private static string Complaints(GH_Document document)
    {
        int errors = 0;
        int warnings = 0;
        StringBuilder first = new();
        List<(string Name, double Ms)> costs = [];

        foreach (IGH_DocumentObject thing in document.Objects)
        {
            if (thing is not IGH_ActiveObject active)
            {
                continue;
            }

            if (active.ProcessorTime.TotalMilliseconds >= 1)
            {
                costs.Add(($"{active.Name} ({active.NickName})", active.ProcessorTime.TotalMilliseconds));
            }

            foreach (string message in active.RuntimeMessages(GH_RuntimeMessageLevel.Error))
            {
                errors++;

                if (first.Length < 400)
                {
                    first.Append(first.Length > 0 ? "," : "").Append(Json.Quote($"{thing.Name}: {message}"));
                }
            }

            warnings += active.RuntimeMessages(GH_RuntimeMessageLevel.Warning).Count;
        }

        StringBuilder slowest = new();

        foreach ((string name, double ms) in costs.OrderByDescending(cost => cost.Ms).Take(5))
        {
            slowest.Append(slowest.Length > 0 ? "," : "")
                .Append("{\"name\":").Append(Json.Quote(name))
                .Append(",\"ms\":").Append(Json.Number((long)ms)).Append('}');
        }

        return $",\"errors\":{Json.Number(errors)},\"warnings\":{Json.Number(warnings)}"
            + (first.Length > 0 ? $",\"first\":[{first}]" : "")
            + (slowest.Length > 0 ? $",\"slowest\":[{slowest}]" : "");
    }
}
