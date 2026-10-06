using System.Net;
using System.Text.Json;

namespace Phenome.Apps.RhinoLink;

/// <summary>
/// The Rhino viewport: what it shows, where it looks, and how to aim it.
/// </summary>
/// <remarks>
/// These verbs are here for the same reason as the plug-in records: the viewport is part of Rhino, and answering
/// from the canvas half required Grasshopper to be running only to capture a Rhino window. None of the three
/// verbs uses Grasshopper.
/// <para>
/// The canvas half keeps its own copies, and a pairing where only that side is current still works. The client
/// asks here first and falls back to the canvas half on a 404.
/// </para>
/// </remarks>
internal static class View
{
    /// <summary>Captures the viewport at the size asked for; see <see cref="Picture"/>.</summary>
    internal static string Screenshot(HttpListenerRequest request) => Picture.Viewport(request, Ui.On);

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
    /// Frames a view without an interactive command. Rhino's <c>Zoom</c> is interactive: run from a script
    /// with an unrecognized magnification, it waits for a pick that never arrives. It holds the UI thread in the
    /// meantime, and every other verb here fails and is reported as busy, although the thread is stuck. Setting
    /// the camera directly asks nothing of the user and cannot block.
    ///
    /// Location and target are set together when both are given: setting one at a time makes Rhino recompute
    /// the other, and the second call undoes part of the first.
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
                // would otherwise discard the placement just made.
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

            // The caller does not ask for clipping planes, and they are refitted here anyway. Moving a camera
            // without them leaves geometry outside a frustum fitted to the old position, and the view returns
            // empty for a cause that does not look like the real one.
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
