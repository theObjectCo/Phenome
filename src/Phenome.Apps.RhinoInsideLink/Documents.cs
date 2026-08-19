using System.Text;

using Rhino;
using Rhino.FileIO;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// Reading, writing and converting documents in a Rhino that was never opened.
/// </summary>
/// <remarks>
/// Every document is opened headless, read or written, and disposed within the one call. Nothing is kept
/// between requests, which is the opposite of the other two links: they answer about a document somebody is
/// looking at, and this one has no such document - the file on disk is the state.
/// <para>
/// Every write suppresses dialogs, and that is not defensive habit. Measured: a headless core with
/// <c>WindowStyle.NoWindow</c> still puts up a modal, and asking for file version 7 on a document holding
/// Rhino 8 data is enough to do it - "the model contains information that cannot be saved in a Rhino 7
/// file", three buttons, nobody there to press one. The write returned false and the server thread would
/// have sat there. An application with no window can still have a window.
/// </para>
/// </remarks>
internal static class Documents
{
    /// <summary>Options for every write: no dialogs, no prompts, nothing that waits for a person.</summary>
    static FileWriteOptions Writing(int version) => new()
    {
        FileVersion = version,
        SuppressDialogBoxes = true,
        SuppressAllInput = true,

        // The path a caller asked for, not a new home for the document: a conversion should not make the
        // source forget where it came from.
        UpdateDocumentPath = false,
    };

    /// <summary>What a document holds, as JSON.</summary>
    internal static string Describe(string path)
    {
        Exists(path);

        using RhinoDoc doc = RhinoDoc.OpenHeadless(path);

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"path\":").Append(Json.Quote(Path.GetFullPath(path)));
        json.Append(",\"name\":").Append(Json.Quote(doc.Name ?? ""));
        json.Append(",\"units\":").Append(Json.Quote(doc.ModelUnitSystem.ToString()));
        json.Append(",\"tolerance\":").Append(Json.Number(doc.ModelAbsoluteTolerance));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));

        json.Append(",\"layers\":[");
        bool first = true;

        foreach (Rhino.DocObjects.Layer layer in doc.Layers)
        {
            if (!first) json.Append(',');
            first = false;
            json.Append("{\"name\":").Append(Json.Quote(layer.Name ?? ""));
            json.Append(",\"visible\":").Append(layer.IsVisible ? "true" : "false");
            json.Append(",\"locked\":").Append(layer.IsLocked ? "true" : "false").Append('}');
        }

        json.Append("],\"contents\":[");
        first = true;

        // Counted by kind rather than listed one by one: a document with forty thousand objects would
        // otherwise answer with forty thousand lines, and the question this verb is asked is "what is in
        // there", not "name everything".
        foreach (IGrouping<string, Rhino.DocObjects.RhinoObject> kind in doc.Objects
            .GroupBy(o => o.ObjectType.ToString())
            .OrderByDescending(g => g.Count()))
        {
            if (!first) json.Append(',');
            first = false;
            json.Append("{\"kind\":").Append(Json.Quote(kind.Key));
            json.Append(",\"count\":").Append(Json.Number(kind.Count()));
            json.Append(",\"named\":").Append(Json.Number(kind.Count(o => !string.IsNullOrWhiteSpace(o.Name)))).Append('}');
        }

        json.Append("]}");

        return json.ToString();
    }

    /// <summary>
    /// Reads one file and writes another, in whatever format the target's extension asks for.
    /// </summary>
    /// <remarks>
    /// The formats are Rhino's, not this assembly's: <c>.3dm</c> goes through the archive writer and anything
    /// else through the exporter registered for that extension. Verified working headless for <c>.stl</c>,
    /// <c>.obj</c>, <c>.dxf</c> and <c>.step</c> - which is worth stating, because the exporters are plugins
    /// and a Rhino with no window is not obviously a Rhino with plugins.
    /// </remarks>
    internal static string Convert(string from, string to, int version)
    {
        Exists(from);

        string target = Path.GetFullPath(to);
        string? folder = Path.GetDirectoryName(target);

        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        using RhinoDoc doc = RhinoDoc.OpenHeadless(from);

        bool asRhino = Path.GetExtension(target).Equals(".3dm", StringComparison.OrdinalIgnoreCase);

        bool written = asRhino
            ? doc.WriteFile(target, Writing(version))
            : doc.Export(target);

        long size = File.Exists(target) ? new FileInfo(target).Length : 0;

        // Rhino answers a bool and the disk answers a size, and they can disagree - an exporter that wrote
        // nothing still returns true for some formats. Both go back, so a caller can tell.
        if (!written && size == 0)
        {
            throw new IOException(
                $"Rhino would not write {target}. Nothing landed on disk. " +
                "An extension Rhino has no exporter for is the usual reason.");
        }

        // An extension Rhino does not recognise does not get refused: Export writes a Rhino file under
        // whatever name it was handed and answers true. Measured - converting to '.zzz' produced a perfectly
        // good .3dm called .zzz, reported as a success. A caller who asked for one format and got another
        // under its name has been lied to, and the lie only surfaces wherever that file is opened next.
        //
        // So the file is asked what it is. A .3dm opens with a fixed banner, which is cheap to read and does
        // not need a list of Rhino's formats kept in step by hand.
        if (!asRhino && size > 0 && LooksLikeRhino(target))
        {
            File.Delete(target);

            throw new IOException(
                $"Rhino has no exporter for '{Path.GetExtension(target)}' - it wrote a Rhino file under that " +
                "name instead, so nothing was kept. Ask for an extension Rhino knows: .3dm, .stl, .obj, " +
                ".dxf, .step and the rest of its export list.");
        }

        StringBuilder json = new();
        json.Append("{\"ok\":true");
        json.Append(",\"from\":").Append(Json.Quote(Path.GetFullPath(from)));
        json.Append(",\"to\":").Append(Json.Quote(target));
        json.Append(",\"wrote\":").Append(written ? "true" : "false");
        json.Append(",\"bytes\":").Append(Json.Number(size));
        json.Append(",\"objects\":").Append(Json.Number(doc.Objects.Count));
        json.Append('}');

        return json.ToString();
    }

    /// <summary>Whether a file opens with the banner every .3dm opens with.</summary>
    /// <remarks>
    /// "3D Geometry File Format" is the first thing in an openNURBS archive, and has been since the format
    /// existed. Read as bytes rather than as text so an encoding guess cannot come into it.
    /// </remarks>
    static bool LooksLikeRhino(string path)
    {
        ReadOnlySpan<byte> banner = "3D Geometry File Format"u8;

        try
        {
            using FileStream file = File.OpenRead(path);

            Span<byte> head = stackalloc byte[banner.Length];

            return file.Read(head) == banner.Length && head.SequenceEqual(banner);
        }
        catch (Exception)
        {
            // Unreadable is not the question being asked; let the caller keep whatever landed.
            return false;
        }
    }

    static void Exists(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"There is no file at {path}.", path);
        }
    }
}
