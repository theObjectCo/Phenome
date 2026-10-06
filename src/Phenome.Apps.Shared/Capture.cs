namespace Phenome.Apps;

/// <summary>
/// Whether a picture is being taken for an agent, for everything in this Rhino that draws over the view.
/// </summary>
/// <remarks>
/// The orange border that shows an agent at work is drawn by the canvas half, in every viewport and on the canvas.
/// It belongs on the screen and not in the picture the agent asked for, where it is part of neither the model nor
/// the definition. Pictures are taken by both halves: <c>screenshot</c> is answered by the Rhino half when it is
/// loaded.
/// <para>
/// Each half compiles its own copy of this file, and a static field would be one per assembly, so a capture in the
/// Rhino half would never reach the border painted by the canvas half. The count is kept in the process's
/// <see cref="AppDomain"/> data instead, which both copies read. Captures run on the UI thread, one at a time,
/// and the count needs no lock.
/// </para>
/// </remarks>
internal static class Capture
{
    private const string Key = "Phenome.Apps.Capture.Depth";

    /// <summary>True while a capture is running.</summary>
    internal static bool Active => AppDomain.CurrentDomain.GetData(Key) is int depth && depth > 0;

    /// <summary>Hides what draws over the view until the returned scope is disposed.</summary>
    internal static IDisposable Quiet()
    {
        Shift(+1);

        return new Scope();
    }

    private static void Shift(int by) =>
        AppDomain.CurrentDomain.SetData(Key, Math.Max(0, (AppDomain.CurrentDomain.GetData(Key) as int? ?? 0) + by));

    private sealed class Scope : IDisposable
    {
        private bool done;

        public void Dispose()
        {
            if (done)
            {
                return;
            }

            done = true;
            Shift(-1);
        }
    }
}
