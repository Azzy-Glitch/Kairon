using System.Net;
using Kairon.Backend.Configuration;

namespace Kairon.Backend.Infrastructure;

public enum PairingEndpointFailure
{
    None,

    /// <summary>RequirePublicBackendUrl is on but Product:BackendUrl is blank, so there is no
    /// address to hand out except the loopback default - which is exactly what that switch exists
    /// to refuse.</summary>
    NotConfigured,

    /// <summary>Not an absolute URI. A relative value has no host at all, so an SDK would resolve
    /// it against whatever it happened to be doing - or store a string it can never send to.</summary>
    NotAbsolute,

    /// <summary>Not https (or, for a loopback-permitted deployment, not http/https either).</summary>
    InsecureScheme,

    /// <summary>A loopback, any-address or "localhost" host, which only names the SDK's OWN
    /// machine once the response leaves this one.</summary>
    LoopbackNotReachable,

    /// <summary>Contains embedded credentials (https://user:pass@host). Those would be persisted
    /// by the SDK and replayed on every request - a credential leak by configuration.</summary>
    ContainsUserInfo,

    /// <summary>Carries a query string. The SDK appends its own paths to this base, so a query here
    /// would end up in the middle of every URL it builds.</summary>
    ContainsQuery,

    /// <summary>Carries a fragment, which is never sent to a server and only corrupts the base.</summary>
    ContainsFragment
}

/// <summary>
/// Validates the address a pairing response hands to an SDK. The SDK persists this value and sends
/// its project API key to it for the life of the installation, so it is checked BEFORE a pairing
/// code is consumed or a credential is issued - a misconfigured deployment must burn neither.
/// </summary>
public static class PairingEndpointPolicy
{
    /// <summary>What the packaged desktop backend actually binds to
    /// (desktop/Kairon.Desktop/MainForm.cs), and the only correct answer for it.</summary>
    public const string DesktopLoopbackDefault = "http://127.0.0.1:8000";

    public static bool TryResolve(ProductOptions options, out string endpoint, out PairingEndpointFailure failure)
    {
        endpoint = string.Empty;

        var configured = options.BackendUrl?.Trim();
        if (string.IsNullOrEmpty(configured))
        {
            if (options.RequirePublicBackendUrl)
            {
                failure = PairingEndpointFailure.NotConfigured;
                return false;
            }

            endpoint = DesktopLoopbackDefault;
            failure = PairingEndpointFailure.None;
            return true;
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
        {
            failure = PairingEndpointFailure.NotAbsolute;
            return false;
        }

        // Checked even on the permissive path: embedded credentials, a query or a fragment are
        // never legitimate in a base address, whatever the deployment shape.
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            failure = PairingEndpointFailure.ContainsUserInfo;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Query))
        {
            failure = PairingEndpointFailure.ContainsQuery;
            return false;
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            failure = PairingEndpointFailure.ContainsFragment;
            return false;
        }

        var https = uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var http = uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

        if (options.RequirePublicBackendUrl)
        {
            if (!https)
            {
                // The SDKs refuse a remote plaintext endpoint anyway (see _endpoint_security.py and
                // the .NET SDK's equivalent), so an http public URL pairs successfully and then
                // never delivers anything.
                failure = PairingEndpointFailure.InsecureScheme;
                return false;
            }

            if (IsOnlyMeaningfulOnTheServersOwnMachine(uri))
            {
                failure = PairingEndpointFailure.LoopbackNotReachable;
                return false;
            }
        }
        else if (!https && !http)
        {
            failure = PairingEndpointFailure.InsecureScheme;
            return false;
        }

        endpoint = configured.TrimEnd('/');
        failure = PairingEndpointFailure.None;
        return true;
    }

    /// <summary>True for an address that names the machine the request came FROM once it leaves
    /// this server: loopback literals, "localhost", and the unspecified/any address.</summary>
    private static bool IsOnlyMeaningfulOnTheServersOwnMachine(Uri uri)
    {
        if (uri.IsLoopback) return true;

        var host = uri.Host;
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // "0.0.0.0" / "[::]" is a bind address, never a destination - an SDK that stored it would
        // have nowhere to send to.
        return IPAddress.TryParse(host.Trim('[', ']'), out var address) &&
               (IPAddress.IsLoopback(address) ||
                address.Equals(IPAddress.Any) ||
                address.Equals(IPAddress.IPv6Any));
    }

    public static string Explain(PairingEndpointFailure failure) => failure switch
    {
        PairingEndpointFailure.NotConfigured =>
            "Product:RequirePublicBackendUrl is enabled but Product:BackendUrl is not set.",
        PairingEndpointFailure.NotAbsolute =>
            "Product:BackendUrl must be an absolute URL including scheme and host.",
        PairingEndpointFailure.InsecureScheme =>
            "Product:BackendUrl must use https when Product:RequirePublicBackendUrl is enabled.",
        PairingEndpointFailure.LoopbackNotReachable =>
            "Product:BackendUrl must be an address SDKs on other machines can reach, not loopback.",
        PairingEndpointFailure.ContainsUserInfo =>
            "Product:BackendUrl must not embed credentials.",
        PairingEndpointFailure.ContainsQuery =>
            "Product:BackendUrl must not contain a query string.",
        PairingEndpointFailure.ContainsFragment =>
            "Product:BackendUrl must not contain a fragment.",
        _ => "Product:BackendUrl is valid."
    };
}
