using System.Net;
using System.Net.Sockets;

namespace Phenome.Apps;

/// <summary>How both halves of the link acquire a free loopback port.</summary>
internal static class Loopback
{
    /// <summary>
    /// The only address anything binds to or is reached on.
    /// </summary>
    /// <remarks>
    /// A constant, because the address appears both in the prefix a listener binds and in text a person reads.
    /// The two must agree, or a log line points at an address nothing is listening on.
    /// </remarks>
    internal const string Address = "127.0.0.1";

    /// <summary>
    /// Binds a listener on an ephemeral loopback port, retrying until one binds.
    /// </summary>
    /// <param name="port">The bound port, set only once a listener is running there.</param>
    /// <remarks>
    /// Asking a socket for a free port and then handing the number to <see cref="HttpListener"/> leaves a gap
    /// between releasing and binding, during which the port can be taken. Two Rhinos starting together can be
    /// handed the same one: the second's bind throws, the caller logs that the link could not start, and the
    /// session ends up without a bridge. The gap cannot be closed (HttpListener will not take an already-open
    /// socket and cannot be asked for port 0). This method detects the failed bind and retries.
    /// <para>
    /// <paramref name="port"/> is an out parameter, returned together with the running listener. A caller
    /// cannot publish a number (in a discovery file or log line) that was never bound.
    /// </para>
    /// <para>
    /// Both halves call this one method. The README beside this file describes how separate copies drifted,
    /// with this retry present in the canvas half only. The race is most likely when two Rhinos start
    /// together, which is the case the Rhino half serves.
    /// </para>
    /// </remarks>
    internal static HttpListener Listen(out int port)
    {
        const int attempts = 12;
        List<string> refusals = [];

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            int candidatePort = FreePort();
            HttpListener candidate = new();
            candidate.Prefixes.Add($"http://{Address}:{candidatePort}/");

            try
            {
                candidate.Start();
            }
            catch (Exception failure)
            {
                // Closed here instead of by a finalizer: a half-open listener would hold the port the next
                // attempt might be handed.
                candidate.Close();
                refusals.Add($"{candidatePort}: {failure.Message}");
                continue;
            }

            port = candidatePort;
            return candidate;
        }

        throw new InvalidOperationException(
            $"No loopback port could be bound in {attempts} attempts. Tried {string.Join("; ", refusals)}");
    }

    private static int FreePort()
    {
        // HttpListener cannot request a free port. A socket on port 0 is given one by the system and releases it.
        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }
}
