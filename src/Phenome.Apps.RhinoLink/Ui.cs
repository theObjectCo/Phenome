namespace Phenome.Apps.RhinoLink;

/// <summary>
/// Runs work on the UI thread that owns Rhino, and explains a wait that ends in a timeout.
/// </summary>
/// <remarks>
/// Shared by the verb classes: everything that touches the document or plug-in manager needs it, and
/// <see cref="Pulse"/> exists because it does not. A timeout says more than "no answer". A long command and an
/// open dialog both look like silence and need opposite responses, and the timeout reuses the message Pulse
/// would give.
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
