using System.Net;

namespace Phenome.Apps;

/// <summary>
/// Keeps a web page from driving the link, which binding to loopback alone does not.
/// </summary>
/// <remarks>
/// The link binds <c>127.0.0.1</c>. That does not limit callers to programs the user already trusts: a
/// browser is a process on this machine, and a page the user merely visits can
/// issue <c>fetch('http://127.0.0.1:53812/place', {mode:'no-cors', ...})</c>. That request leaves the user's
/// own computer, arrives on loopback, and looks exactly like a local client. The attacker does not need to
/// reach the port from outside, only to get the page opened.
/// <para>
/// The API compiles and runs C# inside Rhino. A link reachable from a page allows arbitrary code execution
/// whenever a page is visited while Rhino is open.
/// </para>
/// <para>
/// The defence is a header check, since the address is the same for a page and a local client. Three headers
/// are sent by browsers and by none of the local clients the link serves (the MCP server, the extension, a
/// script, curl):
/// <list type="bullet">
/// <item><c>Origin</c> is sent on cross-origin requests, including the "simple" POST that <c>no-cors</c>
/// allows.</item>
/// <item><c>Sec-Fetch-Site</c> and <c>Sec-Fetch-Dest</c> are sent on every fetch from a page, including
/// same-origin ones, and a page cannot suppress or forge them. The rest of the family is not checked: Node's
/// fetch sends <c>Sec-Fetch-Mode</c>.</item>
/// <item><c>Referer</c> is usually present, but a referrer policy can remove it. It is the weakest of the three
/// and is checked for completeness only.</item>
/// </list>
/// </para>
/// <para>
/// <c>Host</c> is checked separately, against DNS rebinding: a page's own domain is made to resolve to
/// 127.0.0.1, and the browser treats the request as same-origin and sends no <c>Origin</c>. The Host header
/// still carries the attacker's domain name. Requiring a loopback Host refuses the request.
/// </para>
/// <para>
/// A POST must also carry <c>Content-Type: application/json</c>, which a <c>no-cors</c> request cannot set.
/// It blocks the same request a second way and is the stronger of the two checks. Clients send it from 0.32.0;
/// older ones send none or Node's default <c>text/plain</c>, and the refusal says which.
/// </para>
/// </remarks>
internal static class Browser
{
    /// <summary>Why this request must be refused, or <c>null</c> if it may proceed.</summary>
    /// <param name="request">The request as it arrived.</param>
    /// <param name="port">The port this link is listening on, for the <c>Host</c> check.</param>
    internal static string? Refuse(HttpListenerRequest request, int port)
    {
        foreach (string header in new[] { "Origin", "Referer" })
        {
            if (!string.IsNullOrEmpty(request.Headers[header]))
            {
                return $"{header} present: this link answers local programs, not web pages.";
            }
        }

        // Two named headers, not the whole Sec-Fetch- family: **Node's fetch sends Sec-Fetch-Mode**.
        // Rejecting the family would refuse the link's own MCP server and extension on every POST and every
        // poll. This was measured with a listener in front of the real client; curl sends none of these
        // headers and proves nothing about them.
        //
        // Browsers send Site and Dest on every request, and no other client of the link sends them. If a Node
        // release adds one, this refuses the link's own clients. The check is defence in depth, and the
        // content-type check below carries the weight.
        foreach (string header in new[] { "Sec-Fetch-Site", "Sec-Fetch-Dest" })
        {
            if (!string.IsNullOrEmpty(request.Headers[header]))
            {
                return $"{header} present: this link answers local programs, not web pages.";
            }
        }

        // Empty is allowed (HTTP/1.0 and some minimal clients omit Host); present but wrong is not.
        string host = request.Headers["Host"] ?? "";

        if (host.Length != 0 && !Addressed(host, port))
        {
            return $"Host '{host}' is not this link: expected 127.0.0.1:{port}, localhost:{port} or [::1]:{port}.";
        }

        // This is the only check a page cannot satisfy and the only positive one here: the request has to
        // carry a header, where the checks above require one to be absent. A no-cors request may only be
        // text/plain, x-www-form-urlencoded or multipart/form-data. Requiring JSON makes the request
        // non-simple. The browser then sends a preflight first, and a server that never grants preflight ends
        // it there. The checks above fail open if a browser stops sending a given header, and this one fails
        // closed.
        if (request.HttpMethod == "POST")
        {
            string kind = request.Headers["Content-Type"] ?? "";

            if (!kind.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                // The message names the likeliest cause as well as the literal fault. Clients send this header
                // from 0.32.0; older ones send none or Node's text/plain default. The checks above refuse a
                // browser before it gets here. The usual sender is an old client, most often a workspace's
                // .phenome/gh-mcp.js left by a previous extension, on which every GET keeps working.
                bool old = kind.Length == 0 || kind.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase);

                return old
                    ? $"POST needs Content-Type: application/json, and this request carried "
                      + $"{(kind.Length == 0 ? "none" : $"'{kind}'")}, which is what clients older than 0.32.0 "
                      + "send. Most often that is a workspace's .phenome/gh-mcp.js planted by an older "
                      + "extension: update the VS Code extension, then run 'Phenome Link: Teach Agents in "
                      + "This Workspace' again and restart the agent so it loads the new copy."
                    : $"POST needs Content-Type: application/json, not '{kind}'.";
            }
        }

        return null;
    }

    private static bool Addressed(string host, int port) =>
        host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
        || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase)
        || host.Equals($"[::1]:{port}", StringComparison.OrdinalIgnoreCase);
}
