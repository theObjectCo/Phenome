using System.Drawing;
using System.Drawing.Drawing2D;

using Grasshopper.GUI.Canvas;

using Rhino.Display;

using Phenome.Apps.GrasshopperLink.Bridge;

namespace Phenome.Apps.GrasshopperLink;

/// <summary>
/// Shows on screen that an agent, not the person, is driving.
/// </summary>
/// <remarks>
/// An agent moves the same canvas and viewports a user does, and from across the room the two look the
/// same: geometry appears, sliders move, the view jumps. The border tells the person whether to reach for the
/// mouse or wait.
/// <para>
/// While an agent is acting, every Rhino viewport and the Grasshopper canvas get a solid inner border. The
/// border clears eight seconds after the last action, and journal polling alone never shows it.
/// </para>
/// <para>
/// A border is seen without being read. A dialog needs dismissing and a command-line message scrolls away, and
/// both of those demand attention.
/// </para>
/// </remarks>
internal static class Attention
{
    /// <summary>
    /// How long after a request the border stays lit, long enough to cover the gaps between calls.
    /// </summary>
    /// <remarks>
    /// Agents work in bursts with pauses between them. A border that clears during a pause wrongly signals that
    /// the machine is free again.
    /// </remarks>
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(8);

    /// <summary>
    /// The border colour, Object Orange (#ff9800).
    /// </summary>
    /// <remarks>
    /// The house teal reads as the normal brand colour. Its value is too close to Rhino's grey viewport
    /// background and far too close to a white one, and it washes out where it has to be visible.
    /// <para>
    /// The house palette reserves orange for critical calls to action and warnings, which fits "an agent is
    /// driving". Orange stands out against both grey and white backgrounds without looking like an alarm.
    /// </para>
    /// </remarks>
    private static readonly Color Glow = Color.FromArgb(0xFF, 0x98, 0x00);

    /// <summary>
    /// The border width in pixels. The border is solid.
    /// </summary>
    /// <remarks>
    /// A soft glow is either too faint to see or strong enough to read as a painted frame with blurred edges,
    /// and no opacity setting makes a gradient work as a border.
    /// <para>
    /// A solid border takes no room, sits outside the drawing, is clearly visible and looks the same on white and
    /// grey viewports. The visibility of a gradient depends on what is behind it.
    /// </para>
    /// </remarks>
    private const int Thickness = 2;

    private static Conduit? conduit;
    private static System.Timers.Timer? clock;
    private static bool lit;

    /// <summary>The canvas already subscribed for painting. Attach checks it and never subscribes twice.</summary>
    private static GH_Canvas? painted;

    internal static void Start()
    {
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            conduit = new Conduit { Enabled = true };

            // Attach both ways, because neither alone covers every case. This runs on the UI thread after the
            // plugin loads: attaching to ActiveCanvas covers a canvas that already exists, and CanvasCreated covers
            // every canvas created later. ActiveCanvas can be null at this point. With ActiveCanvas alone the
            // handler would then never reach a canvas, and the border would appear in Rhino viewports but never
            // on the canvas.
            Attach(Grasshopper.Instances.ActiveCanvas);
            Grasshopper.Instances.CanvasCreated += Attach;

            // A timer polls the state, because the border must clear when nothing happens and nothing happening
            // raises no event. Twice a second is below the flicker threshold and costs nothing while the border is
            // dark.
            clock = new System.Timers.Timer(500) { AutoReset = true };
            clock.Elapsed += (_, _) => Tick();
            clock.Start();
        });
    }

    /// <summary>Subscribes the border painter to a canvas, once per canvas.</summary>
    private static void Attach(GH_Canvas? canvas)
    {
        if (canvas is null || ReferenceEquals(canvas, painted)) return;

        canvas.CanvasPostPaintOverlay += PaintCanvas;
        painted = canvas;
    }

    internal static void Stop()
    {
        clock?.Stop();
        clock?.Dispose();
        clock = null;

        if (conduit is not null) conduit.Enabled = false;
        conduit = null;
    }

    /// <summary>
    /// Whether an agent is working. Being connected is not enough.
    /// </summary>
    /// <remarks>
    /// Uses LastAction. A paired client polls the journal every couple of seconds whether or not it is acting,
    /// and a border keyed to LastRequest would stay lit for the whole connection.
    /// </remarks>
    private static bool Busy => DateTime.Now - LinkServer.LastAction < Hold;

    private static void Tick()
    {
        bool now = Busy;
        if (now == lit) return;

        lit = now;

        // Redraw only on a change. Redrawing every half second regardless would load a machine that is busy with
        // other work.
        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            Rhino.RhinoDoc.ActiveDoc?.Views.Redraw();
            Grasshopper.Instances.ActiveCanvas?.Refresh();
        });
    }

    private static void PaintCanvas(GH_Canvas canvas)
    {
        if (!Busy) return;

        Graphics? graphics = canvas.Graphics;
        if (graphics is null) return;

        Rectangle frame = canvas.ClientRectangle;
        if (frame.Width <= Thickness * 2 || frame.Height <= Thickness * 2) return;

        // The overlay stage keeps the canvas transform, in document coordinates. The border is drawn in window
        // coordinates; with the transform left on, it would scroll and scale with the definition.
        GraphicsState state = graphics.Save();
        graphics.ResetTransform();

        // Inset by half the pen width. The whole line then falls inside the control, and its edge clips nothing.
        using Pen pen = new(Glow, Thickness);
        graphics.DrawRectangle(
            pen,
            frame.Left + Thickness / 2,
            frame.Top + Thickness / 2,
            frame.Width - Thickness,
            frame.Height - Thickness);

        graphics.Restore(state);
    }

    /// <summary>The same border in every Rhino viewport, drawn in the foreground over the scene.</summary>
    private sealed class Conduit : DisplayConduit
    {
        protected override void DrawForeground(DrawEventArgs e)
        {
            if (!Busy) return;

            var bounds = e.Viewport.Bounds;
            if (bounds.Width <= Thickness * 2 || bounds.Height <= Thickness * 2) return;

            Rectangle frame = new(
                Thickness / 2,
                Thickness / 2,
                bounds.Width - Thickness,
                bounds.Height - Thickness);

            e.Display.Draw2dRectangle(frame, Glow, Thickness, Color.Transparent);
        }
    }
}
