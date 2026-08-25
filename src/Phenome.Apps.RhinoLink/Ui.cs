namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Getting onto the one thread that owns Rhino, and saying why the wait ended when it does not.
/// </summary>
/// <remarks>
/// Shared rather than owned by one verb class, because everything that touches the document or the
/// plug-in manager needs it and <see cref="Pulse"/> exists precisely because it does not. A timeout here
/// is never simply "no answer": a long command and an open dialog both look like silence from the outside
/// and want opposite responses, so the refusal borrows the sentence pulse would have said.
/// </remarks>
internal static class Ui
{
    internal static T On<T>(Func<T> work)
    {
        T result = default!;
        Exception? failure = null;

        using SemaphoreSlim done = new(0, 1);

        Rhino.RhinoApp.InvokeOnUiThread(() =>
        {
            try
            {
                result = work();
            }
            catch (Exception thrown)
            {
                failure = thrown;
            }
            finally
            {
                done.Release();
            }
        });

        if (!done.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException(Pulse.Sentence());
        }

        return failure is null ? result : throw failure;
    }
}
