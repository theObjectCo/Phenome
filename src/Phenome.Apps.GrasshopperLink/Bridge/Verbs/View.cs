using System.Net;
using System.Text;
using System.Text.Json;

using Grasshopper.Kernel;

using Phenome.Apps.GrasshopperLink.Definition;

using static Phenome.Apps.GrasshopperLink.Bridge.Verbs.Plumbing;

namespace Phenome.Apps.GrasshopperLink.Bridge.Verbs;

/// <summary>What is drawn, and where it is looked at from.</summary>
/// <remarks>
/// The canvas as an image, the Rhino viewport as an image, which objects preview, and where the camera
/// stands. None of it changes a definition - a preview flag is how a thing is shown, not what it is.
/// </remarks>
internal static class View
{
    /// <summary>
    /// The Grasshopper canvas as a picture, so an author can see whether their layout reads.
    /// </summary>
    /// <remarks>
    /// Two agents in a row said the same thing: they could see the geometry but never the canvas, so
    /// "is this readable" had to be inferred from coordinates and a lint. Captured through the control's own
    /// DrawToBitmap rather than Grasshopper's export pipeline, which answers a failed render with a modal
    /// message box - a dialog nobody is there to dismiss would hang Rhino behind it.
    /// <para>
    /// Fitted to the whole document for the capture and the view put back afterwards, on the same principle
    /// as the viewport screenshot: the canvas belongs to the human.
    /// </para>
    /// </remarks>
    internal static string CanvasImage(HttpListenerRequest request)
    {
        int width = int.TryParse(request.QueryString["width"], out int asked)
            ? Math.Clamp(asked, 240, 2400)
            : 1200;

        bool fit = !string.Equals(request.QueryString["fit"], "false", StringComparison.OrdinalIgnoreCase);

        string png = OnUi(() =>
        {
            Grasshopper.GUI.Canvas.GH_Canvas canvas = global::Grasshopper.Instances.ActiveCanvas
                ?? throw new InvalidOperationException("There is no canvas - a headless session has no view.");

            float keptZoom = canvas.Viewport.Zoom;
            System.Drawing.PointF keptMid = canvas.Viewport.MidPoint;

            if (fit && canvas.Document is { } document && document.ObjectCount > 0)
            {
                System.Drawing.RectangleF? all = null;

                foreach (IGH_DocumentObject thing in document.Objects)
                {
                    if (thing.Attributes is { } attributes)
                    {
                        all = all is null
                            ? attributes.Bounds
                            : System.Drawing.RectangleF.Union(all.Value, attributes.Bounds);
                    }
                }

                if (all is { } bounds)
                {
                    bounds.Inflate(40, 40);

                    canvas.Viewport.Zoom = Math.Clamp(
                        Math.Min(canvas.Width / bounds.Width, canvas.Height / bounds.Height),
                        0.05f,
                        Grasshopper.GUI.Canvas.GH_Viewport.ZoomDefault);

                    canvas.Viewport.MidPoint = new System.Drawing.PointF(
                        bounds.X + (bounds.Width / 2),
                        bounds.Y + (bounds.Height / 2));
                }
            }

            // White for the capture: the canvas's own grey wash turns to mud at a tenth of the size, and a
            // picture meant for judging a layout should show the layout. Grasshopper's skin is static, so
            // it is put back immediately afterwards.
            System.Drawing.Color keptBack = Grasshopper.GUI.Canvas.GH_Skin.canvas_back;
            System.Drawing.Color keptGrid = Grasshopper.GUI.Canvas.GH_Skin.canvas_grid;
            System.Drawing.Color keptEdge = Grasshopper.GUI.Canvas.GH_Skin.canvas_edge;

            try
            {
                Grasshopper.GUI.Canvas.GH_Skin.canvas_back = System.Drawing.Color.White;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_grid = System.Drawing.Color.FromArgb(16, 0, 0, 0);
                Grasshopper.GUI.Canvas.GH_Skin.canvas_edge = System.Drawing.Color.White;

                canvas.Refresh();

                using System.Drawing.Bitmap full = new(canvas.Width, canvas.Height);

                canvas.DrawToBitmap(full, new System.Drawing.Rectangle(0, 0, canvas.Width, canvas.Height));

                int height = Math.Max(120, (int)((double)width / Math.Max(1, full.Width) * full.Height));

                // Onto white: the canvas grid is a pale wash that turns to mud when scaled down, and a
                // picture meant for judging a layout should show the layout, not the tablecloth.
                using System.Drawing.Bitmap scaled = new(width, height);

                using (System.Drawing.Graphics paint = System.Drawing.Graphics.FromImage(scaled))
                {
                    paint.Clear(System.Drawing.Color.White);
                    paint.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    paint.DrawImage(full, 0, 0, width, height);
                }

                using MemoryStream bytes = new();

                scaled.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);

                return Convert.ToBase64String(bytes.ToArray());
            }
            finally
            {
                Grasshopper.GUI.Canvas.GH_Skin.canvas_back = keptBack;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_grid = keptGrid;
                Grasshopper.GUI.Canvas.GH_Skin.canvas_edge = keptEdge;

                canvas.Viewport.Zoom = keptZoom;
                canvas.Viewport.MidPoint = keptMid;
                canvas.Refresh();
            }
        });

        return $"{{\"ok\":true,\"png\":{Json.Quote(png)}}}";
    }

    /// <summary>The eyes, kept cheap on purpose: a low resolution says plenty and costs the reader little.</summary>
    internal static string Screenshot(HttpListenerRequest request)
    {
        int width = int.TryParse(request.QueryString["width"], out int asked)
            ? Math.Clamp(asked, 160, 1920)
            : 640;

        bool frame = !string.Equals(request.QueryString["zoomExtents"], "false", StringComparison.OrdinalIgnoreCase);

        string png = OnUi(() =>
        {
            Rhino.Display.RhinoView view = Rhino.RhinoDoc.ActiveDoc?.Views.ActiveView
                ?? throw new InvalidOperationException("There is no Rhino view to capture.");

            System.Drawing.Size full = view.ClientRectangle.Size;
            int height = Math.Max(120, (int)((double)width / Math.Max(1, full.Width) * Math.Max(1, full.Height)));

            // Framed for the capture, put back after: the picture should show the geometry, but the
            // camera belongs to the human and stays where they left it.
            Rhino.DocObjects.ViewportInfo? kept = frame
                ? new Rhino.DocObjects.ViewportInfo(view.ActiveViewport)
                : null;

            // The target is kept apart: restoring the projection alone recomputes it from the frustum, and
            // the human would come back to their own camera aimed somewhere new.
            Rhino.Geometry.Point3d target = view.ActiveViewport.CameraTarget;

            if (frame)
            {
                view.ActiveViewport.ZoomExtents();
            }

            try
            {
                using System.Drawing.Bitmap bitmap = view.CaptureToBitmap(new System.Drawing.Size(width, height))
                    ?? throw new InvalidOperationException("The viewport would not be captured.");

                using MemoryStream bytes = new();

                bitmap.Save(bytes, System.Drawing.Imaging.ImageFormat.Png);

                return Convert.ToBase64String(bytes.ToArray());
            }
            finally
            {
                if (kept is not null)
                {
                    view.ActiveViewport.SetViewProjection(kept, updateTargetLocation: false);
                    view.ActiveViewport.SetCameraTarget(target, updateCameraLocation: false);
                    view.Redraw();
                }
            }
        });

        return $"{{\"ok\":true,\"png\":{Json.Quote(png)}}}";
    }

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
                ?? throw new InvalidOperationException("There is no canvas to move - headless sessions have no view.");

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
    /// A finished definition previews everything it ever computed: the boxes a difference already ate, the
    /// construction curves, the profile that was extruded away. The product is in there somewhere, and the
    /// human is left picking it out of the scaffolding - or worse, reads the scaffolding as the answer.
    /// <para>
    /// The colours already say which geometry was ever meant to be looked at: red is what gets baked as the
    /// product, yellow is preview-only, and grey and blue are machinery. So a sweep over the whole document
    /// leaves exactly the outlets of the red and yellow groups drawing - what those groups yield - and
    /// hides everything else, machinery and intermediates alike. Naming one group instead quiets that one
    /// on its own terms, whatever colour it wears, which is how you look inside a function again.
    /// </para>
    /// <para>
    /// A verb rather than doctrine because doing it by hand is a click per component and the next edit
    /// undoes it.
    /// </para>
    /// </remarks>
    internal static string Quiet(JsonDocument request)
    {
        string author = Author(request);
        Guid? only = Field(request, "id") is { } id ? Guid.Parse(id) : null;
        bool on = request.RootElement.TryGetProperty("on", out JsonElement flag) && AsBool(flag);

        string answer = OnUi(() =>
        {
            GH_Document document = ActiveDocument()
                ?? throw new InvalidOperationException("There is no document.");

            List<Grasshopper.Kernel.Special.GH_Group> groups =
                [.. document.Objects.OfType<Grasshopper.Kernel.Special.GH_Group>()
                    .Where(group => only is null || group.InstanceGuid == only)];

            if (groups.Count == 0)
            {
                throw new KeyNotFoundException(only is null
                    ? "There are no groups on the canvas."
                    : $"No group {only} on the canvas.");
            }

            System.Text.StringBuilder json = new("{\"ok\":true,\"groups\":[");
            bool first = true;

            // Across all groups, so the document is marked changed only if some flag actually moved. A preview
            // flag is saved in the .gh file, so flipping one is a real change - but this verb is run over an
            // already-quiet document often enough that marking unconditionally would produce a save prompt for
            // having looked.
            int flipped = 0;

            foreach (Grasshopper.Kernel.Special.GH_Group group in groups)
            {
                (_, List<IGH_Param> outlets) = Signature.Ports(document, group);

                // Named on its own, a group is quieted on its own terms. Swept over the whole document,
                // only the groups whose colour says "this is geometry to look at" keep their outlets.
                bool shows = only is not null || Shows(group.Colour);

                HashSet<Guid> drawing = shows
                    ? [.. outlets.Select(outlet => outlet.InstanceGuid)]
                    : [];

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

                    // Counted from where things end up, not from what this call altered. Reporting the delta
                    // is what made an answer of zero ambiguous between "nothing needed quieting" and "nothing
                    // is drawing" -- and on the restoring path the drawing count was hardcoded to zero, which
                    // reads as failure when the truth is that everything came back.
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
                json.Append(",\"changed\":").Append(Json.Number(changed)).Append('}');

                flipped += changed;
            }

            if (flipped > 0)
            {
                Changed(document);
            }

            document.ExpirePreview(true);

            global::Grasshopper.Instances.ActiveCanvas?.Refresh();
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();

            return json.Append("]}").ToString();
        });

        Journal.Append(author, "preview", $",\"on\":{(on ? "true" : "false")}");

        return answer;
    }

    /// <summary>
    /// Whether a group's colour says its geometry is meant to be seen: red baked as the product, yellow
    /// there to be looked at. Grey is a function and blue is a knob; neither owes the viewport anything.
    /// </summary>
    private static bool Shows(System.Drawing.Color colour)
    {
        // Grasshopper's own colour picker rounds, and so does a human eye - the same tolerance review uses.
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
    /// The way to frame a view deliberately. Rhino's own <c>Zoom</c> is an interactive command: run it
    /// from a script with a magnification it does not recognise and it sits waiting for a pick that will
    /// never arrive, which holds the UI thread and so fails every other verb here at once, with a message
    /// about being busy rather than about being stuck. Setting the camera outright asks nothing of the
    /// user and cannot wait for them.
    ///
    /// Location and target are set together when both are given, because setting one at a time makes
    /// Rhino recompute the other and the second call then undoes half of the first.
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

            // Clipping planes are not the caller's business but they are the caller's problem: moving a
            // camera without them leaves geometry outside a frustum that was fitted to where it used to
            // be, and the view comes back empty for a reason that looks nothing like the cause.
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
