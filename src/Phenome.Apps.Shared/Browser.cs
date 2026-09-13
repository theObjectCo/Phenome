using System.Net;

namespace Phenome.Apps;

/// <summary>Keeps a web page from driving the link, which binding to loopback does not.</summary>
/// <remarks>
/// The link binds <c>127.0.0.1</c>, and it is tempting to read that as "only this machine, therefore only
/// something the user already trusts". It is not. A browser is a process on this machine, and a page the
/// user merely visits can issue <c>fetch('http://127.0.0.1:53812/place', {mode:'no-cors', ...})</c>. The
/// request leaves the user's own computer, arrives on loopback, and looks exactly like a local client. The
/// address never enters into it: an attacker does not need to reach the port from outside, only to get the
/// page opened.
/// <para>
/// That matters here more than it would almost anywhere else, because this API compiles and runs C# inside
/// Rhino. Reachable from a page means arbitrary code execution by visiting a website, while Rhino happens to
/// be open - which for somebody working is all day.
/// </para>
/// <para>
/// The defence is therefore a header, not an address. Three of them are sent by browsers and by nothing
/// else we speak to:
/// <list type="bullet">
/// <item><c>Origin</c> - on cross-origin requests, including the "simple" POST that <c>no-cors</c> allows.</item>
/// <item><c>Sec-Fetch-Site</c> and its siblings - on every fetch from a page, same-origin ones included, and
/// a page cannot suppress or forge them.</item>
/// <item><c>Referer</c> - usually present, and a referrer policy can remove it, so it is the weakest of the
/// three and is here for completeness rather than as the load-bearing one.</item>
/// </list>
/// A local client - the MCP server, the extension, a script, curl - sends none of them.
/// </para>
/// <para>
/// <c>Host</c> is checked separately and answers a different attack: DNS rebinding, where a page's own
/// domain is made to resolve to 127.0.0.1 so that the browser considers the request same-origin and sends
/// no <c>Origin</c> at all. The header then still carries the attacker's name rather than ours, so a link
/// that insists on being addressed as loopback refuses it.
/// </para>
/// <para>
/// What is deliberately <em>not</em> here yet: requiring <c>Content-Type: application/json</c>, which a
/// <c>no-cors</c> request cannot set and which would therefore close the same door a second time. It is a
/// better check than any of the above, and it is absent because our own clients do not send the header
/// either - Node's fetch defaults to <c>text/plain</c> when handed a string body. Requiring it before the
/// clients send it would break every existing installation the moment a plugin is updated ahead of its
/// extension, which on someone else's machine is the normal state. It goes in the version after this one,
/// with the clients.
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

        // Two named headers rather than the whole Sec-Fetch- family, and the difference was found by
        // measurement rather than reading: **Node's fetch sends Sec-Fetch-Mode**, so rejecting the family
        // would have refused our own MCP server and extension - every POST and every poll. The first
        // version of this did exactly that and was caught by putting a listener in front of the real
        // client instead of testing with curl, which sends none of these and proves nothing about them.
        //
        // Site and Dest are sent by browsers on every request and by nothing else we speak to. If a Node
        // release ever adds one, this refuses our own clients again - so it is defence in depth and the
        // content type below is the check that carries the weight.
        foreach (string header in new[] { "Sec-Fetch-Site", "Sec-Fetch-Dest" })
        {
            if (!string.IsNullOrEmpty(request.Headers[header]))
            {
                return $"{header} present: this link answers local programs, not web pages.";
            }
        }

        // Empty is fine: HTTP/1.0 and some minimal clients omit it. Present and wrong is not.
        string host = request.Headers["Host"] ?? "";

        if (host.Length != 0 && !Addressed(host, port))
        {
            return $"Host '{host}' is not this link: expected 127.0.0.1:{port} or localhost:{port}.";
        }

        // The one check a page cannot satisfy, and the only one of these that is positive: the request has
        // to prove something rather than merely lack a header. A no-cors request may carry text/plain,
        // x-www-form-urlencoded or multipart/form-data and nothing else; asking for JSON makes it
        // non-simple, so the browser sends a preflight first, and a server that does not answer preflights
        // with permission ends the matter there. The checks above depend on browsers continuing to send
        // headers they send today, and would fail open if one ever stopped. This one fails closed.
        if (request.HttpMethod == "POST")
        {
            string kind = request.Headers["Content-Type"] ?? "";

            if (!kind.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            {
                // Named for the likeliest cause rather than the literal fault: from 0.32.0 the clients
                // send this header, so a POST without it is almost always an extension that was not
                // updated alongside the plugin, and the person reading this needs to be told that rather
                // than left with a header name.
                return kind.Length == 0
                    ? "POST needs Content-Type: application/json. A client that does not send it is "
                      + "older than this plugin - update the VS Code extension to match."
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
