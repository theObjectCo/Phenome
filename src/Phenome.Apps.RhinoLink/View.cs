using System.Net;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The Rhino viewport: what it shows, where it looks, and where to point it.
/// </summary>
/// <remarks>
/// Here for the reason the plug-in records are: a viewport is Rhino's, and answering about it from the
/// canvas half meant a Grasshopper had to be running to photograph a Rhino window. Nothing in these three
/// ever touched Grasshopper - they were simply written where the server happened to be at the time.
/// <para>
/// The canvas half keeps its copies, so a pairing where only that side is current still works; the client
/// asks here first and falls back there on a 404.
/// </para>
/// </remarks>
internal static class View
{
    /// <summary>The eyes, kept cheap on purpose: a low resolution says plenty and costs the reader little.</summary>
    internal static string Screenshot(HttpListenerRequest request)
    {
        int width = int.TryParse(request.QueryString["width"], out int asked)
            ? Math.Clamp(asked, 160, 1920)
            : 640;

        bool frame = !string.Equals(request.QueryString["zoomExtents"], "false", StringComparison.OrdinalIgnoreCase);

        string png = Ui.On(() =>
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

    /// <summary>Where the active viewport is looking.</summary>
    internal static string ReadCamera() => Ui.On(Camera);

    private static string Camera()
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
    internal static string AimCamera(string payload)
    {
        using JsonDocument request = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(payload) ? "{}" : payload);

        Rhino.Geometry.Point3d? location = OptionalPoint(request, "location");
        Rhino.Geometry.Point3d? target = OptionalPoint(request, "target");
        Rhino.Geometry.Point3d? up = OptionalPoint(request, "up");

        double? lens = request.RootElement.TryGetProperty("lens", out JsonElement lensField)
            && lensField.ValueKind is JsonValueKind.Number
                ? lensField.GetDouble()
                : null;

        string? projection = request.RootElement.TryGetProperty("projection", out JsonElement field)
            && field.ValueKind == JsonValueKind.String
                ? field.GetString()
                : null;

        return Ui.On(() =>
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

            return Camera();
        });
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
