using System.Text;

using Grasshopper.Kernel;

namespace Phenome.Apps.GrasshopperLink.Definition;

/// <summary>
/// Search over every component this installation actually has.
/// </summary>
/// <remarks>
/// Queries are evaluated against the component server in the current process, which knows the installed plugins.
/// A static catalogue would be large, would change with every version and would miss them. The top matches are
/// instantiated to read their actual parameter lists, the same way a component is instantiated when it is added
/// to a canvas.
/// </remarks>
internal static class Catalogue
{
    private const int Detailed = 5;
    private const int Listed = 25;

    internal static string Search(string query)
    {
        List<IGH_ObjectProxy> ranked = [.. global::Grasshopper.Instances.ComponentServer.ObjectProxies
            .Where(proxy => !proxy.Obsolete && proxy.Kind == GH_ObjectType.CompiledObject)
            .Select(proxy => (Proxy: proxy, Rank: Rank(proxy, query)))
            .Where(scored => scored.Rank > 0)
            .OrderByDescending(scored => scored.Rank)
            .ThenBy(scored => scored.Proxy.Desc.Name.Length)
            .Take(Listed)
            .Select(scored => scored.Proxy)];

        StringBuilder json = new("{\"components\":[");

        for (int i = 0; i < ranked.Count; i++)
        {
            if (i > 0)
            {
                json.Append(',');
            }

            Describe(ranked[i], withParameters: i < Detailed, json);
        }

        return json.Append("]}").ToString();
    }

    /// <summary>
    /// Ranks name matches above nickname matches and nickname matches above description matches. Within the name,
    /// an exact match ranks above a prefix match and a prefix match above a substring match.
    /// </summary>
    private static int Rank(IGH_ObjectProxy proxy, string query)
    {
        string name = proxy.Desc.Name ?? "";
        string nickname = proxy.Desc.NickName ?? "";
        string description = proxy.Desc.Description ?? "";

        if (name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 60;
        }

        if (nickname.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 50;
        }

        if (description.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 20;
        }

        return 0;
    }

    private static void Describe(IGH_ObjectProxy proxy, bool withParameters, StringBuilder into)
    {
        into.Append("{\"name\":").Append(Json.Quote(proxy.Desc.Name ?? ""));
        into.Append(",\"nickname\":").Append(Json.Quote(proxy.Desc.NickName ?? ""));
        into.Append(",\"guid\":").Append(Json.Quote(proxy.Guid.ToString()));
        into.Append(",\"category\":").Append(Json.Quote($"{proxy.Desc.Category} › {proxy.Desc.SubCategory}"));
        into.Append(",\"description\":").Append(Json.Quote(proxy.Desc.Description ?? ""));

        if (withParameters)
        {
            try
            {
                if (proxy.CreateInstance() is IGH_Component component)
                {
                    Parameters("inputs", component.Params.Input, into);
                    Parameters("outputs", component.Params.Output, into);
                }
            }
            catch (Exception)
            {
                // If instantiation fails, return the component listing without parameters. The parameter details are
                // an addition to the basic search result.
            }
        }

        into.Append('}');
    }

    private static void Parameters(string side, IReadOnlyList<IGH_Param> parameters, StringBuilder into)
    {
        into.Append($",\"{side}\":[");

        for (int i = 0; i < parameters.Count; i++)
        {
            if (i > 0)
            {
                into.Append(',');
            }

            into.Append("{\"name\":").Append(Json.Quote(parameters[i].Name));
            into.Append(",\"type\":").Append(Json.Quote(parameters[i].TypeName));

            if (parameters[i].Optional)
            {
                into.Append(",\"optional\":true");
            }

            into.Append('}');
        }

        into.Append(']');
    }
}
