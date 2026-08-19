using System.Net;
using System.Net.Sockets;

namespace Phenome.Apps;

/// <summary>How both halves of the link get a port nobody else is on.</summary>
internal static class Loopback
{
    /// <summary>
    /// Binds a listener on an ephemeral loopback port, retrying until one sticks.
    /// </summary>
    /// <param name="port">The port it settled on - set only once a listener is actually running there.</param>
    /// <remarks>
    /// Asking a socket for a free port and then handing the number to <see cref="HttpListener"/> leaves a gap
    /// between letting go and binding, and in that gap the port can be taken. Two Rhinos starting together
    /// can be handed the same one: the second's bind throws, the caller logs that the link could not start,
    /// and the session is silently without a bridge. The gap cannot be closed -- HttpListener will not accept
    /// a socket that is already open, and it cannot be asked for port zero -- so the answer is to notice and
    /// try again rather than to trust the first number.
    /// <para>
    /// <paramref name="port"/> is an out parameter rather than a property here for a reason: the caller
    /// publishes it, in a discovery file or a log line, and it must not be possible to publish a number that
    /// was never bound. Returning them together makes that ordering the only one available.
    /// </para>
    /// <para>
    /// Shared because it was fixed in the canvas half and left as the racing version in the Rhino half - the
    /// same drift the README beside this file is about. Two Rhinos starting together is precisely when the
    /// Rhino half matters, so that is where the race was most likely to be lost.
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
            candidate.Prefixes.Add($"http://127.0.0.1:{candidatePort}/");

            try
            {
                candidate.Start();
            }
            catch (Exception failure)
            {
                // Closed rather than left to a finalizer: a half-open listener would hold the very port the
                // next attempt might be handed.
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
        // The system picks a free port; HttpListener cannot ask for one itself, so a socket asks and lets go.
        TcpListener probe = new(IPAddress.Loopback, 0);

        probe.Start();

        int port = ((IPEndPoint)probe.LocalEndpoint).Port;

        probe.Stop();

        return port;
    }
}
