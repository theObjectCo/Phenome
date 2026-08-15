using System.Globalization;
using System.Text;

namespace Phenome.Apps.RhinoLink;

/// <summary>The two things every hand-built JSON needs: a safe string and an invariant number.</summary>
/// <remarks>
/// Built by hand rather than with a serializer for the same reason the transcriber does it: the shapes here
/// are small, assembled from live Grasshopper objects that no serializer should be pointed at, and the
/// protocol is a contract - what goes on the wire is exactly what is written here, not what a library
/// decides an object graph looks like this version.
/// </remarks>
internal static class Json
{
    internal static string Quote(string value)
    {
        StringBuilder quoted = new(value.Length + 2);

        quoted.Append('"');

        foreach (char letter in value)
        {
            switch (letter)
            {
                case '"':
                    quoted.Append("\\\"");
                    break;
                case '\\':
                    quoted.Append("\\\\");
                    break;
                case '\n':
                    quoted.Append("\\n");
                    break;
                case '\r':
                    quoted.Append("\\r");
                    break;
                case '\t':
                    quoted.Append("\\t");
                    break;
                default:
                    if (letter < ' ')
                    {
                        quoted.Append("\\u").Append(((int)letter).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        quoted.Append(letter);
                    }

                    break;
            }
        }

        return quoted.Append('"').ToString();
    }

    internal static string Number(double value) => value.ToString(CultureInfo.InvariantCulture);

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Re-laid with indentation, for the reader who is a person pasting into a prompt.</summary>
    internal static string Indented(string json)
    {
        using System.Text.Json.JsonDocument parsed = System.Text.Json.JsonDocument.Parse(json);

        return System.Text.Json.JsonSerializer.Serialize(
            parsed,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
