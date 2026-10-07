using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>What is drawn, and from where it is viewed.</summary>
/// <remarks>
/// These verbs capture the canvas and the Rhino viewport as images, set which objects preview, and place the
/// camera. None of this changes a definition: a preview flag is how a thing is shown, not what it is.
/// </remarks>
internal static class View
{
    /// <summary>
    /// The Grasshopper canvas as an image, for checking whether the layout is legible and for documentation.
    /// </summary>
    /// <remarks>
    /// Without it an agent sees the geometry but not the canvas, and legibility has to be inferred from
    /// coordinates and a lint. The image is captured through the control's DrawToBitmap. Grasshopper's export
    /// pipeline reports a failed render with a modal message box, and a dialog that cannot be dismissed would
    /// hang Rhino.
    /// <para>
    /// DrawToBitmap draws only what fits in the window, and a picture larger than the window used to be the
    /// window's picture stretched, with the text on the components blurred. The picture is now drawn in tiles of
    /// the window's size at the zoom the asked size needs, and the tiles are joined. Each tile is drawn in
    /// Grasshopper's export mode, which leaves out the widgets that sit at a fixed place in the window and keeps a
    /// group's name above its frame however near the tile's edge it falls.
    /// </para>
    /// <para>
    /// Fitted to the whole document for the capture and restored afterwards, as with the viewport screenshot: the
    /// canvas belongs to the user.
    /// </para>
    /// </remarks>
    internal static string CanvasImage(HttpListenerRequest request)
    {
        Picture.Asked asked = Picture.Read(request);
        bool fit = !string.Equals(request.QueryString["fit"], "false", StringComparison.OrdinalIgnoreCase);

        List<Guid> chosen = [.. (request.QueryString["ids"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => Guid.TryParse(id, out Guid parsed)
                ? parsed
                : throw new ArgumentException($"'{id}' in 'ids' is not an object id."))];

        float margin = float.TryParse(
            request.QueryString["margin"],
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out float given) && given >= 0 ? given : 40;

        return OnUi(() =>
        {
            Grasshopper.GUI.Canvas.GH_Canvas canvas = global::Grasshopper.Instances.ActiveCanvas
                ?? throw new InvalidOperationException("There is no canvas: a headless session has no view.");

            // A minimised editor shrinks the canvas to nothing, and GDI+ rejects a zero-size bitmap with
            // "Parameter is not valid." The window belongs to the user and is not restored here.
            if (canvas.Width < 1 || canvas.Height < 1)
            {
                throw new InvalidOperationException(
                    $"The canvas is {canvas.Width} x {canvas.Height} pixels, which is what a minimised "
                    + "Grasshopper window gives, and there is nothing to draw. Ask the user to restore the "
                    + "Grasshopper window, then ask again.");
            }

            float keptZoom = canvas.Viewport.Zoom;
            System.Drawing.PointF keptMid = canvas.Viewport.MidPoint;

            // What to draw, in document coordinates: the whole document, or what the window shows now.
            System.Drawing.RectangleF subject = new(
                keptMid.X - (canvas.Width / keptZoom / 2),
                keptMid.Y - (canvas.Height / keptZoom / 2),
                canvas.Width / keptZoom,
                canvas.Height / keptZoom);

            // The chosen objects, or the whole document, with the margin around them. A group counts with its
            // name, which is drawn above its frame.
            if ((chosen.Count > 0 || fit) && canvas.Document is { } document && document.ObjectCount > 0)
            {
                System.Drawing.RectangleF? all = null;

                IEnumerable<IGH_DocumentObject> framed = chosen.Count > 0
                    ? chosen.Select(id => document.FindObject(id, topLevelOnly: true)
                        ?? throw new KeyNotFoundException($"{id} is not on the canvas."))
                    : document.Objects;

                foreach (IGH_DocumentObject thing in framed)
                {
                    if (Arrange.Drawn(thing) is { } drawn)
                    {
                        all = all is null ? drawn : System.Drawing.RectangleF.Union(all.Value, drawn);
                    }
                }

                if (all is { } bounds)
                {
                    bounds.Inflate(margin, margin);
                    subject = bounds;
                }
            }
            else if (chosen.Count > 0)
            {
                throw new InvalidOperationException("There is no document to find those ids in.");
            }

            System.Drawing.Size size = Picture.SizeFor(asked, subject.Width / subject.Height, 1200);

            // Pixels per document unit. Without a size asked for, bounded so that a tiny document does not come
            // back as three huge components; the subject then sits in the middle of a white picture. With a size
            // asked for, the subject fills it up to Grasshopper's own largest zoom: a small group captured 4800
            // pixels across used to stop at 4 pixels per unit, about 2000 pixels of group in a white field.
            float most = asked.Width is null && asked.Height is null
                ? 4f
                : Grasshopper.GUI.Canvas.GH_Viewport.ZoomMaximum;

            float zoom = Math.Clamp(Math.Min(size.Width / subject.Width, size.Height / subject.Height), 0.02f, most);

            // The document point at the picture's top left corner, with the subject centred.
            System.Drawing.PointF origin = new(
                subject.X + (subject.Width / 2) - (size.Width / zoom / 2),
                subject.Y + (subject.Height / 2) - (size.Height / zoom / 2));

            // A plain white ground for the capture: the canvas grey wash becomes indistinct when scaled down, and
            // the image is for judging the layout. Grasshopper's own ground also draws the edge of its page at
            // (0, 0), a shadow and a hatch across negative coordinates, and a picture that framed the origin
            // carried that edge through the definition. The monochrome ground is a fill and nothing else, and the
            // grid is drawn over it by Grid. Grasshopper's skin is static and is restored afterwards.
            bool keptMono = Grasshopper.GUI.Canvas.GH_Skin.canvas_mono;
            System.Drawing.Color keptMonoColour = Grasshopper.GUI.Canvas.GH_Skin.canvas_mono_color;

            try
            {
                Grasshopper.GUI.Canvas.GH_Skin.canvas_mono = true;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_mono_color = System.Drawing.Color.White;
                canvas.CanvasPaintBackground += Grid;

                int tileWidth = canvas.Width;
                int tileHeight = canvas.Height;

                using System.Drawing.Bitmap picture = new(size.Width, size.Height);
                using System.Drawing.Graphics paint = System.Drawing.Graphics.FromImage(picture);

                paint.Clear(System.Drawing.Color.White);

                canvas.Viewport.Zoom = zoom;

                // Without the agent-at-work border, which belongs on the screen and not in the picture.
                using (Capture.Quiet())
                {
                    for (int top = 0; top < size.Height; top += tileHeight)
                    {
                        for (int left = 0; left < size.Width; left += tileWidth)
                        {
                            canvas.Viewport.MidPoint = new System.Drawing.PointF(
                                origin.X + ((left + (tileWidth / 2f)) / zoom),
                                origin.Y + ((top + (tileHeight / 2f)) / zoom));

                            // Drawn the way Grasshopper draws for an export. On the screen a group's name is a
                            // balloon kept inside the window: next to an edge it is pushed sideways or flipped
                            // below the frame, and in a tiled picture every tile edge is a window edge. An export
                            // places the balloon above the frame wherever it is, and leaves out the widgets that
                            // sit at a fixed place in the window, such as the zoom control.
                            using System.Drawing.Bitmap tile = canvas.GetCanvasScreenBuffer(
                                Grasshopper.GUI.Canvas.GH_CanvasMode.Export)
                                ?? throw new InvalidOperationException(
                                    "Grasshopper could not draw the canvas into a picture.");

                            paint.DrawImageUnscaled(tile, left, top);
                        }
                    }
                }

                return Picture.Answer(picture, "canvas", asked);
            }
            finally
            {
                canvas.CanvasPaintBackground -= Grid;

                Grasshopper.GUI.Canvas.GH_Skin.canvas_mono = keptMono;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_mono_color = keptMonoColour;

                canvas.Viewport.Zoom = keptZoom;
                canvas.Viewport.MidPoint = keptMid;
                canvas.Refresh();
            }
        });
    }

    /// <summary>A faint grid over the capture's white ground, the same on both sides of the origin.</summary>
    /// <remarks>
    /// Spaced as the user's canvas is, and faded with the zoom as Grasshopper fades its own, so that a picture of
    /// a large document is not covered in lines.
    /// </remarks>
    private static void Grid(Grasshopper.GUI.Canvas.GH_Canvas canvas)
    {
        int alpha = 16 * Grasshopper.GUI.Canvas.GH_Canvas.ZoomFadeLow / 255;
        int column = Grasshopper.GUI.Canvas.GH_Skin.canvas_grid_col;
        int row = Grasshopper.GUI.Canvas.GH_Skin.canvas_grid_row;

        if (alpha < 2 || column < 1 || row < 1 || canvas.Graphics is not { } graphics)
        {
            return;
        }

        System.Drawing.RectangleF region = canvas.Viewport.VisibleRegion;
        using System.Drawing.Pen pen = new(System.Drawing.Color.FromArgb(alpha, 0, 0, 0));

        for (float x = MathF.Floor(region.Left / column) * column; x <= region.Right; x += column)
        {
            graphics.DrawLine(pen, x, region.Top, x, region.Bottom);
        }

        for (float y = MathF.Floor(region.Top / row) * row; y <= region.Bottom; y += row)
        {
            graphics.DrawLine(pen, region.Left, y, region.Right, y);
        }
    }

    /// <summary>Captures the viewport at the size asked for; see <see cref="Picture"/>.</summary>
    internal static string Screenshot(HttpListenerRequest request) => Picture.Viewport(request, OnUi);

    internal static string Zoom(JsonDocument request)
    {
        string author = Author(request);

        List<Guid> asked = request.RootElement.TryGetProperty("ids", out JsonElement ids)
            ? [.. ids.EnumerateArray().Select(id => Guid.Parse(id.GetString()!))]
            : throw new ArgumentException("zoom needs 'ids'.");

        OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            Grasshopper.GUI.Canvas.GH_Canvas canvas = global::Grasshopper.Instances.ActiveCanvas
                ?? throw new InvalidOperationException("There is no canvas to move: headless sessions have no view.");

            System.Drawing.RectangleF? union = null;

            foreach (Guid id in asked)
            {
                if (document.FindObject(id, topLevelOnly: true) is { Attributes: { } attributes })
                {
                    union = union is null
                        ? attributes.Bounds
                        : System.Drawing.RectangleF.Union(union.Value, attributes.Bounds);
                }
            }

            if (union is not { } bounds)
            {
                throw new KeyNotFoundException("None of those ids are on the canvas.");
            }

            bounds.Inflate(40, 40);

            canvas.Viewport.Zoom = Math.Clamp(
                Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height),
                0.1f,
                Grasshopper.GUI.Canvas.GH_Viewport.ZoomDefault);

            canvas.Viewport.MidPoint = new System.Drawing.PointF(
                bounds.X + (bounds.Width / 2),
                bounds.Y + (bounds.Height / 2));

            canvas.Refresh();

            return true;
        });

        Journal.Append(author, "zoom", $",\"count\":{Json.Number(asked.Count)}");

        return "{\"ok\":true}";
    }

    /// <summary>
    /// Quiets the preview: only the outlets of the red and yellow groups draw, and nothing else does.
    /// </summary>
    /// <remarks>
    /// This leaves only the intended output drawing. A finished definition otherwise previews every intermediate
    /// result: consumed cutting boxes, construction curves, and profiles that were extruded away.
    /// <para>
    /// Group colours define the rule: red is baked output, yellow is preview-only geometry, and grey and blue are
    /// machinery. A document-wide sweep leaves only the outlets of red and yellow groups drawing and hides all other
    /// objects. Naming a group keeps that group's outlets drawing, whatever its colour.
    /// </para>
    /// <para>
    /// It is a verb because setting each component by hand is slow and the next edit can reset it.
    /// </para>
    /// <para>
    /// <b>An id may name a group or a single object, and <c>ids</c> accepts either.</b> Group and document scope
    /// alone miss the common case where one intermediate component floods the viewport while its group must keep
    /// drawing. A facade of 960 panels interpolated through 24 points each gives 23,040 preview markers, and batch
    /// selection is what makes the feature practical there.
    /// </para>
    /// <para>
    /// One verb handles both cases. A separate object verb would duplicate the API and require callers to know the
    /// object kind before asking. Only the group sweep has a member-selection policy.
    /// </para>
    /// <para>
    /// <b>The document-wide sweep also processes ungrouped objects.</b> Objects outside every group are not outlets
    /// of a coloured group, and the sweep hides them. On a document with no groups, it disables all preview output.
    /// </para>
    /// <para>
    /// <b>A named object with no preview is skipped.</b> It is returned in <c>skipped</c>, and the rest of the
    /// batch proceeds. An id absent from the canvas still rejects the whole list, because that indicates a stale
    /// caller list.
    /// </para>
    /// </remarks>
    internal static string Quiet(JsonDocument request)
    {
        string author = Author(request);
        bool on = request.RootElement.TryGetProperty("on", out JsonElement flag) && AsBool(flag);

        List<Guid> asked = [];

        if (Field(request, "id") is { } one)
        {
            asked.Add(Guid.Parse(one));
        }

        if (request.RootElement.TryGetProperty("ids", out JsonElement many)
            && many.ValueKind == JsonValueKind.Array)
        {
            asked.AddRange(many.EnumerateArray().Select(each => Guid.Parse(each.GetString()!)));
        }

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            List<Grasshopper.Kernel.Special.GH_Group> groups = [];
            List<IGH_PreviewObject> singles = [];
            List<IGH_PreviewObject> ungrouped = [];
            List<IGH_DocumentObject> skipped = [];

            if (asked.Count == 0)
            {
                groups = [.. document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>()];

                HashSet<Guid> grouped = [.. groups.SelectMany(group => Signature.Members(document, group))];

                ungrouped = [.. document.Objects
                    .Where(thing => !grouped.Contains(thing.InstanceGuid))
                    .OfType<IGH_PreviewObject>()
                    .Where(thing => thing.IsPreviewCapable)];
            }
            else
            {
            // Validate all ids before changing flags, and report all missing ids together: the caller fixes the
            // list once.
                List<string> missing = [];

                foreach (Guid id in asked)
                {
                    switch (document.FindObject(id, topLevelOnly: true))
                    {
                        case Grasshopper.Kernel.Special.GH_Group group:
                            groups.Add(group);
                            break;

                        case IGH_PreviewObject { IsPreviewCapable: true } thing:
                            singles.Add(thing);
                            break;

                        case { } other:
                            skipped.Add(other);
                            break;

                        default:
                            missing.Add(id.ToString());
                            break;
                    }
                }

                if (missing.Count > 0)
                {
                    throw new KeyNotFoundException(
                        $"{missing.Count} of {asked.Count} id(s) are not on the canvas, and nothing was quieted: "
                        + string.Join(", ", missing));
                }
            }

            System.Text.StringBuilder json = new("{\"ok\":true,\"groups\":[");
            bool first = true;

            // Count changed flags across all groups and mark the document modified only if one changed. Preview
            // flags are saved in the .gh file, but running this on an already-quiet document must not create a save
            // prompt.
            int flipped = 0;

            foreach (Grasshopper.Kernel.Special.GH_Group group in groups)
            {
                (_, List<IGH_Param> outlets) = Signature.Ports(document, group);

                // A named group is processed by its own membership. A document-wide sweep preserves outlets only
                // for groups whose colour marks their output as shown geometry.
                bool shows = asked.Count > 0 || Shows(group.Colour);

                HashSet<Guid> drawing = shows
                    ? [.. outlets.Select(outlet => outlet.InstanceGuid)]
                    : [];

                // A group with no outlet still has final output: its members whose results are not read inside the
                // group. Without this, a red group ending in a component has no drawable output.
                bool byEnd = shows && outlets.Count == 0;

                if (byEnd)
                {
                    drawing = Ends(document, group);
                }

                int quieted = 0;
                int showing = 0;
                int changed = 0;

                foreach (Guid member in Signature.Members(document, group))
                {
                    if (document.FindObject(member, topLevelOnly: true)
                        is not IGH_PreviewObject { IsPreviewCapable: true } thing)
                    {
                        continue;
                    }

                    bool hide = !on && !drawing.Contains(member);

                    // Report the resulting state, not this call's delta. A count of zero then unambiguously means
                    // no member is drawing. A delta would make the restore path look like a failure.
                    if (hide)
                    {
                        quieted++;
                    }
                    else
                    {
                        showing++;
                    }

                    if (thing.Hidden == hide)
                    {
                        continue;
                    }

                    changed++;
                    thing.Hidden = hide;
                }

                if (!first)
                {
                    json.Append(',');
                }

                first = false;

                json.Append("{\"id\":").Append(Json.Quote(group.InstanceGuid.ToString()));
                json.Append(",\"name\":").Append(Json.Quote(group.NickName ?? ""));
                json.Append(",\"hidden\":").Append(Json.Number(quieted));
                json.Append(",\"drawing\":").Append(Json.Number(showing));
                json.Append(",\"changed\":").Append(Json.Number(changed));

                if (byEnd && !on)
                {
                    json.Append(",\"kept\":\"no outlet, so the members nothing else in the group reads\"");
                }

                json.Append('}');

                flipped += changed;
            }

            // Objects named individually have no member-selection policy; set the preview flag directly. This
            // provides the granularity needed to hide one intermediate component while its group continues drawing.
            json.Append("],\"objects\":[");
            first = true;

            foreach (IGH_PreviewObject thing in singles)
            {
                if (thing.Hidden != !on)
                {
                    thing.Hidden = !on;
                    flipped++;
                }

                if (!first)
                {
                    json.Append(',');
                }

                first = false;

                IGH_DocumentObject named = (IGH_DocumentObject)thing;

                json.Append("{\"id\":").Append(Json.Quote(named.InstanceGuid.ToString()));
                json.Append(",\"name\":").Append(Json.Quote(named.NickName ?? named.Name ?? ""));
                json.Append(",\"drawing\":").Append(on ? "true" : "false").Append('}');
            }

            json.Append(']');

            // Count ungrouped objects but do not list them individually: on an ungrouped document this would repeat
            // the caller's full object list.
            if (asked.Count == 0)
            {
                int changed = 0;

                foreach (IGH_PreviewObject thing in ungrouped)
                {
                    if (thing.Hidden != !on)
                    {
                        thing.Hidden = !on;
                        changed++;
                    }
                }

                json.Append(",\"ungrouped\":{\"hidden\":").Append(Json.Number(on ? 0 : ungrouped.Count));
                json.Append(",\"drawing\":").Append(Json.Number(on ? ungrouped.Count : 0));
                json.Append(",\"changed\":").Append(Json.Number(changed)).Append('}');

                flipped += changed;
            }

            if (skipped.Count > 0)
            {
                json.Append(",\"skipped\":[");
                first = true;

                foreach (IGH_DocumentObject other in skipped)
                {
                    if (!first)
                    {
                        json.Append(',');
                    }

                    first = false;

                    json.Append("{\"id\":").Append(Json.Quote(other.InstanceGuid.ToString()));
                    json.Append(",\"name\":").Append(Json.Quote(other.NickName ?? other.Name ?? ""));
                    json.Append(",\"why\":\"draws nothing, so there is no preview to quiet\"}");
                }

                json.Append(']');
            }

            if (flipped > 0)
            {
                Changed(document);
            }

            document.ExpirePreview(true);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();

            return json.Append('}').ToString();
        });

        Journal.Append(author, "preview", $",\"on\":{(on ? "true" : "false")}");

        return answer;
    }

    /// <summary>
    /// The members of a group whose output no other member reads: what the group computes last.
    /// </summary>
    private static HashSet<Guid> Ends(GH_Document document, Grasshopper.Kernel.Special.GH_Group group)
    {
        HashSet<Guid> inside = Signature.Members(document, group);
        HashSet<Guid> ends = [];

        foreach (Guid member in inside)
        {
            if (document.FindObject(member, topLevelOnly: true) is not IGH_PreviewObject { IsPreviewCapable: true } thing)
            {
                continue;
            }

            bool readInside = OutputsOf((IGH_DocumentObject)thing)
                .SelectMany(output => output.Recipients)
                .Any(reader => inside.Contains((reader.Attributes?.GetTopLevel?.DocObject ?? reader).InstanceGuid));

            if (!readInside)
            {
                ends.Add(member);
            }
        }

        return ends;
    }

    /// <summary>
    /// Whether a group colour marks geometry intended for display: red is baked output and yellow is preview-only.
    /// Grey functions and blue inputs are not expected to draw.
    /// </summary>
    private static bool Shows(System.Drawing.Color colour)
    {
        // The tolerance accommodates rounding by Grasshopper's colour picker; it matches the tolerance used by review.
        static bool Near(int one, int other) => Math.Abs(one - other) <= 12;

        return (Near(colour.R, 255) && Near(colour.G, 60) && Near(colour.B, 60))
            || (Near(colour.R, 255) && Near(colour.G, 220) && Near(colour.B, 0));
    }

    /// <summary>Where the active viewport is looking.</summary>
    internal static string ReadCamera()
    {
        Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc?.Views.ActiveView
            ?? throw new InvalidOperationException("There is no Rhino view.");

        Rhino.Display.RhinoViewport viewport = view.ActiveViewport;
        System.Drawing.Size size = view.ClientRectangle.Size;

        return "{\"ok\":true"
            + $",\"view\":{Json.Quote(viewport.Name ?? string.Empty)}"
            + $",\"projection\":{Json.Quote(viewport.IsParallelProjection ? "parallel" : "perspective")}"
            + $",\"location\":{Point(viewport.CameraLocation)}"
            + $",\"target\":{Point(viewport.CameraTarget)}"
            + $",\"up\":{Vector(viewport.CameraUp)}"
            + $",\"lens\":{Json.Number(viewport.Camera35mmLensLength)}"
            + $",\"width\":{Json.Number(size.Width)}"
            + $",\"height\":{Json.Number(size.Height)}"
            + "}";
    }

    /// <summary>
    /// Aims the active viewport, changing only what was asked for.
    /// </summary>
    /// <remarks>
    /// Rhino's <c>Zoom</c> command is interactive: scripted use can wait for a selection that never arrives and
    /// block the UI thread; other verbs then report that Rhino is busy. Setting the viewport properties directly
    /// does not require interaction.
    ///
    /// Location and target are applied together when both are supplied. Applied separately, each makes Rhino
    /// recompute the other value, and the second call can undo part of the first.
    /// </remarks>
    internal static string AimCamera(JsonDocument request)
    {
        string author = Author(request);

        Rhino.Geometry.Point3d? location = OptionalPoint(request, "location");
        Rhino.Geometry.Point3d? target = OptionalPoint(request, "target");
        Rhino.Geometry.Point3d? up = OptionalPoint(request, "up");

        double? lens = request.RootElement.TryGetProperty("lens", out JsonElement lensField)
            && lensField.ValueKind is JsonValueKind.Number
                ? lensField.GetDouble()
                : null;

        string? projection = Field(request, "projection");

        string state = OnUi(() =>
        {
            Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc?.Views.ActiveView
                ?? throw new InvalidOperationException("There is no Rhino view.");

            Rhino.Display.RhinoViewport viewport = view.ActiveViewport;

            if (projection is not null)
            {
                bool parallel = projection.Equals("parallel", StringComparison.OrdinalIgnoreCase);
                bool perspective = projection.Equals("perspective", StringComparison.OrdinalIgnoreCase);

                if (!parallel && !perspective)
                {
                    throw new ArgumentException("camera projection must be 'perspective' or 'parallel'.");
                }

                // Changed before the camera is placed: switching projection rebuilds the frustum, which
                // would otherwise discard the placement that had just been made.
                if (parallel)
                {
                    viewport.ChangeToParallelProjection(symmetricFrustum: true);
                }
                else
                {
                    viewport.ChangeToPerspectiveProjection(symmetricFrustum: true, lensLength: 50);
                }
            }

            if (up is { } upPoint)
            {
                viewport.CameraUp = new Rhino.Geometry.Vector3d(upPoint);
            }

            if (location is { } from && target is { } to)
            {
                viewport.SetCameraLocations(to, from);
            }
            else if (location is { } only)
            {
                viewport.SetCameraLocation(only, updateTargetLocation: false);
            }
            else if (target is { } aim)
            {
                viewport.SetCameraTarget(aim, updateCameraLocation: false);
            }

            if (lens is { } millimetres)
            {
                viewport.Camera35mmLensLength = millimetres;
            }

            // Adjust clipping planes whenever the camera changes. Otherwise geometry can fall outside a frustum
            // sized for the previous camera position, producing an apparently empty view.
            viewport.SetClippingPlanes(Rhino.RhinoDoc.ActiveDoc.Objects.BoundingBox);

            view.Redraw();
            return ReadCamera();
        });

        Journal.Append(author, "camera", string.Empty);

        return state;
    }

    private static Rhino.Geometry.Point3d? OptionalPoint(JsonDocument request, string name)
    {
        if (!request.RootElement.TryGetProperty(name, out JsonElement field)
            || field.ValueKind != JsonValueKind.Array
            || field.GetArrayLength() < 3)
        {
            return null;
        }

        return new Rhino.Geometry.Point3d(
            field[0].GetDouble(),
            field[1].GetDouble(),
            field[2].GetDouble());
    }

    private static string Point(Rhino.Geometry.Point3d p) =>
        $"[{Json.Number(p.X)},{Json.Number(p.Y)},{Json.Number(p.Z)}]";

    private static string Vector(Rhino.Geometry.Vector3d v) =>
        $"[{Json.Number(v.X)},{Json.Number(v.Y)},{Json.Number(v.Z)}]";
}
