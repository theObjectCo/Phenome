using System.Reflection;
using System.Text;

using Grasshopper.Kernel;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Reads and writes the source of script components, both generations, without referencing either.
/// </summary>
/// <remarks>
/// Rhino 8 has two C# script components living side by side: the RhinoCode one
/// (<c>BaseScriptComponent</c>, with <c>TryGetSource</c>/<c>SetSource</c>) and the legacy one
/// (<c>Component_CSNET_Script</c>, whose <c>ScriptSource</c> carries the code in parts). Both are foreign
/// assemblies this plugin must not reference - the members were read out of them with a decompiler, and
/// reflection reaches them at run time. Which generation a component is travels in the answer, so a client
/// knows what dialect of source it is holding.
/// </remarks>
internal static class Scripts
{
    /// <summary>Every script component on the canvas, with its generation.</summary>
    internal static string List(GH_Document? document)
    {
        StringBuilder json = new("{\"scripts\":[");
        bool first = true;

        foreach (IGH_DocumentObject thing in document?.Objects ?? Enumerable.Empty<IGH_DocumentObject>())
        {
            string? generation = GenerationOf(thing);

            if (generation is null)
            {
                continue;
            }

            if (!first)
            {
                json.Append(',');
            }

            first = false;

            json.Append("{\"id\":").Append(Json.Quote(thing.InstanceGuid.ToString()));
            json.Append(",\"name\":").Append(Json.Quote(thing.Name));
            json.Append(",\"nickname\":").Append(Json.Quote(thing.NickName));
            json.Append(",\"generation\":").Append(Json.Quote(generation)).Append('}');
        }

        return json.Append("]}").ToString();
    }

    /// <summary>The source of one script component.</summary>
    internal static string Read(GH_Document? document, Guid id)
    {
        IGH_DocumentObject thing = Find(document, id);

        string source = GenerationOf(thing) switch
        {
            "rhinocode" => ReadRhinoCode(thing),
            "legacy" => ReadLegacy(thing),
            _ => throw new ArgumentException($"{thing.Name} is not a script component."),
        };

        return $"{{\"ok\":true,\"generation\":{Json.Quote(GenerationOf(thing)!)},\"source\":{Json.Quote(source)}}}";
    }

    /// <summary>
    /// New source into one script component, one solve, and the component's own complaints back.
    /// </summary>
    internal static string Write(GH_Document? document, Guid id, string source)
    {
        IGH_DocumentObject thing = Find(document, id);

        switch (GenerationOf(thing))
        {
            case "rhinocode":
                thing.GetType().GetMethod("SetSource", [typeof(string)])!.Invoke(thing, [source]);
                break;

            case "legacy":
                object holder = thing.GetType().GetProperty("ScriptSource")!.GetValue(thing)!;

                holder.GetType().GetProperty("ScriptCode")!.SetValue(holder, source);
                break;

            default:
                throw new ArgumentException($"{thing.Name} is not a script component.");
        }

        thing.ExpireSolution(false);
        document!.NewSolution(false);

        // The push's whole feedback: what the component itself says after compiling and running the new
        // source - the same words its balloon would show, delivered to whoever cannot see the balloon.
        StringBuilder json = new("{\"ok\":true");

        if (thing is IGH_ActiveObject active)
        {
            Append(json, "errors", active.RuntimeMessages(GH_RuntimeMessageLevel.Error));
            Append(json, "warnings", active.RuntimeMessages(GH_RuntimeMessageLevel.Warning));
        }

        return json.Append('}').ToString();
    }

    private static void Append(StringBuilder json, string name, IList<string> messages)
    {
        if (messages.Count == 0)
        {
            return;
        }

        json.Append($",\"{name}\":[");

        for (int i = 0; i < messages.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            json.Append(Json.Quote(messages[i]));
        }

        json.Append(']');
    }

    private static string ReadRhinoCode(IGH_DocumentObject thing)
    {
        MethodInfo ask = thing.GetType().GetMethod("TryGetSource", [typeof(string).MakeByRefType()])!;
        object?[] arguments = [null];

        return (bool)ask.Invoke(thing, arguments)! && arguments[0] is string source
            ? source
            : "";
    }

    private static string ReadLegacy(IGH_DocumentObject thing)
    {
        object holder = thing.GetType().GetProperty("ScriptSource")!.GetValue(thing)!;

        return holder.GetType().GetProperty("ScriptCode")?.GetValue(holder) as string ?? "";
    }

    /// <summary>Which script component this is, if it is one at all - decided by shape, not by name.</summary>
    private static string? GenerationOf(IGH_DocumentObject thing)
    {
        Type type = thing.GetType();

        if (type.GetMethod("TryGetSource", [typeof(string).MakeByRefType()]) is not null
            && type.GetMethod("SetSource", [typeof(string)]) is not null)
        {
            return "rhinocode";
        }

        if (type.GetProperty("ScriptSource")?.PropertyType.GetProperty("ScriptCode") is not null)
        {
            return "legacy";
        }

        return null;
    }

    private static IGH_DocumentObject Find(GH_Document? document, Guid id) =>
        document?.FindObject(id, topLevelOnly: true)
        ?? throw new KeyNotFoundException($"No object {id} on the canvas.");
}
