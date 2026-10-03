using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Rhino.Runtime.InProcess;

namespace Phenome.Apps.RhinoInsideLink;

/// <summary>
/// A Rhino core running in this process, with no window and no user to interact with it.
/// </summary>
/// <remarks>
/// Two things must happen, in order, before any RhinoCommon type is touched. The resolver tells the runtime
/// where the managed RhinoCommon is; the native search path tells Windows where its opennurbs half is. Without
/// the second, the first loads and then fails on its first call with a DLL initialisation error that names
/// nothing useful.
/// <para>
/// The resolver affects only assemblies loaded <em>after</em> it runs. Every caller must keep RhinoCommon types
/// out of the method that calls <see cref="Start"/>, because the JIT resolves a method's types when it compiles
/// the method, before its first line runs.
/// </para>
/// </remarks>
public sealed class HeadlessRhino : IDisposable
{
    readonly RhinoCore _core;
    readonly int _owner;

    HeadlessRhino(RhinoCore core)
    {
        _core = core;

        // The constructing thread owns Rhino, and every document access must return to it. The thread is
        // recorded and not assumed to be thread 1, because a host may start the core from a thread of its own.
        _owner = System.Environment.CurrentManagedThreadId;
    }

    /// <summary>The directory where the installed Rhino keeps RhinoCommon.dll and its native dependencies.</summary>
    public static string SystemDirectory { get; private set; } = string.Empty;

    /// <summary>
    /// Prepares assembly resolution and starts the core. Call from a method that names no RhinoCommon type.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static HeadlessRhino Start()
    {
        Prepare();
        return Create();
    }

    // Separate and never inlined: this is the first method to name a RhinoCommon type, and compiling it is what
    // makes the runtime search, by which point the resolver already points to the right place.
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

        // The managed resolver does not cover the native side; LoadLibrary reads these two paths instead.
        SetDllDirectory(system);
        Environment.SetEnvironmentVariable("PATH", system + ";" + Environment.GetEnvironmentVariable("PATH"));

        SystemDirectory = system;
    }

    readonly System.Collections.Concurrent.BlockingCollection<Action> _queue = new();

    /// <summary>
    /// Runs work on the thread that owns the Rhino core and waits for its result.
    /// </summary>
    /// <remarks>
    /// The headless counterpart of marshalling onto Rhino's UI thread. Requests arrive on worker threads, and a
    /// document belongs to the thread that started the core, and every access to one goes through here.
    /// <para>
    /// The queue is this class's own. <c>RhinoCore.InvokeInHostContext</c> is the obvious choice and does not
    /// work here: it throws <see cref="InvalidOperationException"/> both while the host thread pumps
    /// <c>DoEvents</c> by hand and while it is in <c>RhinoCore.Run</c>. <c>Run</c> itself returns
    /// <c>int.MinValue</c> immediately under <see cref="WindowStyle.NoWindow"/>, because it is a message loop
    /// for a window and there is none. Direct calls on the host thread work; only the marshalling is missing.
    /// </para>
    /// <para>
    /// An exception is returned to the caller and does not kill the server thread. A verb that asks for a
    /// missing file must fail as one request, and the server must keep running.
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
    /// Serves queued work on this thread until <see cref="Stop"/>, giving Rhino an idle turn between items.
    /// </summary>
    /// <remarks>
    /// Must be called from the thread that started the core and nowhere else. The idle turn lets Rhino run its
    /// own housekeeping, including the command-line capture drain that <c>/console</c> reads.
    /// </remarks>
    public void Serve()
    {
        if (System.Environment.CurrentManagedThreadId != _owner)
        {
            throw new InvalidOperationException(
                "Serve has to run on the thread that started the core, which is the thread the Rhino core runs on.");
        }

        foreach (Action work in _queue.GetConsumingEnumerable())
        {
            work();

            // The idle turn costs little and is skipped when more work is waiting. A queue with a backlog is
            // drained first, and housekeeping waits.
            if (_queue.Count == 0)
            {
                _core.DoIdle();
            }
        }
    }

    /// <summary>Lets <see cref="Serve"/> return once the work already queued has finished.</summary>
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
