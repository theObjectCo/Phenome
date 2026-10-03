using System.Text;

using Grasshopper.Kernel;

namespace Phenome.Apps.GrasshopperLink.Bridge;

/// <summary>
/// Writes canvas events to the journal.
/// </summary>
/// <remarks>
/// The source is Grasshopper's own document events: objects added and deleted, documents opened and closed,
/// and solutions ending. A solution-end entry carries a summary of what went red, and an agent learns that the
/// document has errors without reading the whole state. Entries authored here use the author <c>canvas</c>:
/// they describe what the document did, whatever caused it. A client that needs to know who caused it reads
/// the verbs the server journals alongside.
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
    /// Error and warning counts and the slowest components. With them the journal answers "why is this slow"
    /// as well as "did the solve finish".
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
