using System.Collections.Concurrent;

namespace Phenome.Geometry.Modules;

/// <summary>
/// A place to leave geometry for a pair of eyes. Code deep inside a solve drops shapes here under a
/// channel name, and whatever is watching — a Grasshopper component, a test — draws them.
/// </summary>
/// <remarks>
/// <para>
/// This is the one deliberately stateful thing in the kernel, and it exists because the alternative is
/// worse: threading an output list through every intermediate call, or printing coordinates and reading
/// them like tea leaves. An intermediate region that fails to be what you assumed is a picture, not a
/// number, and until you can see it you are guessing.
/// </para>
/// <para>
/// It is a debugging aid and nothing else. Nothing in a solve may read it back and behave differently —
/// that would make a factory impure and break memoization. Clear a channel when you start filling it, or
/// two runs of the same code hand the watcher twice the geometry and the second picture is a lie.
/// </para>
/// </remarks>
public static class DebugGeometry
{
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<object>> Channels = new();

    /// <summary>Leaves one shape on a channel, creating the channel if this is the first.</summary>
    /// <param name="channel">What to file it under; the watcher shows one channel at a time.</param>
    /// <param name="shape">A kernel shape — a point, polyline, mesh, line, circle, whatever is being looked at.</param>
    public static void Add(string channel, object shape)
    {
        if (shape is null) return;

        Channels.GetOrAdd(channel ?? "", _ => new ConcurrentQueue<object>()).Enqueue(shape);
    }

    /// <summary>Leaves several shapes on a channel, in order.</summary>
    public static void AddRange(string channel, IEnumerable<object> shapes)
    {
        if (shapes is null) return;

        foreach (object shape in shapes) Add(channel, shape);
    }

    /// <summary>Everything left on a channel, oldest first; empty when nothing ever was.</summary>
    public static IReadOnlyList<object> Read(string channel) =>
        Channels.TryGetValue(channel ?? "", out ConcurrentQueue<object>? queue)
            ? queue.ToArray()
            : [];

    /// <summary>Every channel that has been written to, in the order they were first written.</summary>
    public static IReadOnlyList<string> ChannelNames() => Channels.Keys.ToArray();

    /// <summary>How many shapes a channel holds.</summary>
    public static int Count(string channel) =>
        Channels.TryGetValue(channel ?? "", out ConcurrentQueue<object>? queue) ? queue.Count : 0;

    /// <summary>Empties one channel and forgets it. Call this before filling it, not after.</summary>
    public static void Clear(string channel) => Channels.TryRemove(channel ?? "", out _);

    /// <summary>Empties every channel.</summary>
    public static void ClearAll() => Channels.Clear();
}
