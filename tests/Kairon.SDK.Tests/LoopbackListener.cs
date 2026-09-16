using System.Net;

namespace Kairon.SDK.Tests;

/// <summary>
/// Claims a loopback port for an <see cref="HttpListener"/> by ATTEMPTING the registration and
/// retrying on failure - never by asking a throwaway TcpListener for a "free" port first.
///
/// On Windows, HttpListener registers a URL through http.sys, a genuinely separate namespace from a
/// raw TCP socket bind, so a port TcpListener reports as free can still be refused here
/// ("conflicts with an existing registration on the machine"). Probing first only narrows that race;
/// attempting the actual resource being claimed removes it. Observed for real twice in this
/// repository - once on a shared CI runner, and once locally when a credential-concurrency test
/// that starts twelve listeners at once ran alongside another suite - so every loopback stub in
/// this project goes through here rather than reinventing the probe.
/// </summary>
internal static class LoopbackListener
{
    internal static (HttpListener Listener, string Url) Claim()
    {
        HttpListenerException? last = null;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var url = $"http://127.0.0.1:{Random.Shared.Next(20000, 60000)}";
            var listener = new HttpListener();
            listener.Prefixes.Add(url + "/");
            try
            {
                listener.Start();
                return (listener, url);
            }
            catch (HttpListenerException exception)
            {
                last = exception;
            }
        }

        throw last!;
    }
}
