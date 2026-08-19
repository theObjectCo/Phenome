using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Rhino.Runtime.InProcess;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// A Rhino core running in this process, with no window and nobody to click it.
/// </summary>
/// <remarks>
/// Two things have to happen before a single RhinoCommon type is touched, and they have to happen in
/// this order. The resolver teaches the runtime where the managed RhinoCommon lives, and the native
/// search path teaches Windows where its opennurbs half lives - without the second, the first loads
/// fine and then fails on the first call with a DLL initialisation error that names nothing useful.
/// <para>
/// Because the resolver only helps assemblies loaded <em>after</em> it runs, every caller must keep
/// RhinoCommon types out of the method that calls <see cref="Start"/>. The JIT resolves a method's
/// types when it compiles the method, which is before its first line runs.
/// </para>
/// </remarks>
public sealed class HeadlessRhino : IDisposable
{
    readonly RhinoCore _core;
    readonly int _owner;

    HeadlessRhino(RhinoCore core)
    {
        _core = core;

        // Whoever constructed this is the thread Rhino belongs to, and every document touch has to come back
        // here. Recorded rather than assumed to be thread 1, because a host that starts the core from a
        // thread of its own is a reasonable thing to do.
        _owner = System.Environment.CurrentManagedThreadId;
    }

    /// <summary>Where the installed Rhino keeps RhinoCommon.dll and its native neighbours.</summary>
    public static string SystemDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// Prepares assembly resolution and starts the core. Call from a method that mentions no
    /// RhinoCommon type of its own.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static HeadlessRhino Start()
    {
        Prepare();
        return Create();
    }

    // Separate, and never inlined, because this is the first method that names a RhinoCommon type:
    // compiling it is what sends the runtime looking, and by here the resolver has been told where.
    [MethodImpl(MethodImplOptions.NoInlining)]
    static HeadlessRhino Create() =>
        new(new RhinoCore(["/nosplash", "/notemplate"], WindowStyle.NoWindow));

    /// <summary>Resolver and native search path, without starting anything.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Prepare()
    {
        if (SystemDirectory.Length > 0) return;

        RhinoInside.Resolver.Initialize();

        var system = RhinoInside.Resolver.RhinoSystemDirectory;
        if (string.IsNullOrEmpty(system) || !Directory.Exists(system))
            throw new InvalidOperationException("No installed Rhino was found for Rhino.Inside to load.");

        // The managed resolver does not cover the native side; LoadLibrary reads these two instead.
        SetDllDirectory(system);
        Environment.SetEnvironmentVariable("PATH", system + ";" + Environment.GetEnvironmentVariable("PATH"));

        SystemDirectory = system;
    }

    readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

    /// <summary>
    /// Runs a piece of work on the thread that owns the Rhino core, and waits for its answer.
    /// </summary>
    /// <remarks>
    /// The headless counterpart of marshalling onto Rhino's UI thread: requests arrive on worker threads and
    /// a document belongs to the thread that started the core, so every touch of one comes through here.
    /// <para>
    /// A queue of our own rather than <c>RhinoCore.InvokeInHostContext</c>, which is the obvious answer and
    /// does not work here. Measured: it throws <see cref="InvalidOperationException"/> both while the host
    /// thread pumps <c>DoEvents</c> by hand and while it sits in <c>RhinoCore.Run</c> - and <c>Run</c> itself
    /// returns <c>int.MinValue</c> immediately under <see cref="WindowStyle.NoWindow"/>, because it is a
    /// message loop for a window and there is no window. Direct calls on the host thread work perfectly, so
    /// what is missing is only the marshalling, and that is a queue.
    /// </para>
    /// <para>
    /// An exception is carried back to the caller rather than killing the server thread, because a verb that
    /// asks for a file that is not there must fail as a request, not as an outage.
    /// </para>
    /// </remarks>
    public T Invoke<T>(Func<T> work)
    {
        if (System.Environment.CurrentManagedThreadId == _owner)
        {
            return work();
        }

        using ManualResetEventSlim done = new(false);
        T answer = default!;
        Exception? failure = null;

        _queue.Add(() =>
        {
            try
            {
                answer = work();
            }
            catch (Exception thrown)
            {
                failure = thrown;
            }
            finally
            {
                done.Set();
            }
        });

        done.Wait();

        return failure is null ? answer : throw failure;
    }

    /// <inheritdoc cref="Invoke{T}"/>
    public void Invoke(Action work) => Invoke<bool>(() => { work(); return true; });

    /// <summary>
    /// Serves queued work on this thread until <see cref="Stop"/>, giving Rhino its idle turn between items.
    /// </summary>
    /// <remarks>
    /// Called from the thread that started the core and from nowhere else - that is the whole contract. The
    /// idle turn is what lets Rhino do its own housekeeping, including the command-line capture drain that
    /// <c>/console</c> reads.
    /// </remarks>
    public void Serve()
    {
        if (System.Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException(
                "Serve has to run on the thread that started the core; that is the thread Rhino belongs to.");
        }

        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            work();

            // Cheap, and skipped entirely when there is more work waiting: a queue with a backlog wants
            // draining, not housekeeping.
            if (_queue.Count == 0)
            {
                _core.DoIdle();
            }
        }
    }

    /// <summary>Lets <see cref="Serve"/> return once the work already queued has been done.</summary>
    public void Stop() => _queue.CompleteAdding();

    /// <inheritdoc/>
    public void Dispose()
    {
        Stop();
        _queue.Dispose();
        _core.Dispose();
    }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetDllDirectory(string path);
}
