using System.Buffers.Binary;
using System.Net.Sockets;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;

namespace Kairon.SDK;

/// <summary>
/// Optional machine attestation. The localhost Agent only receives an opaque, one-use challenge
/// id; the SDK bearer credential goes exclusively to the configured non-redirecting backend.
/// A missing Agent leaves telemetry observable but unscoped for remediation.
/// </summary>
internal static class AgentMachineProof
{
    internal const int ProductionPort = 47891;

    /// <summary>The installed Agent's fixed loopback port. Settable only inside this assembly and
    /// its test friends: the SDK test assembly points it away from a real Agent running on the
    /// developer's machine (see its module initializer); production never changes it.</summary>
    internal static int DefaultPort { get; set; } = ProductionPort;

    /// <summary>The backend identity the Agent compares with its own configured endpoint - the
    /// exact base the telemetry is posted to, path base included (scheme://host[:port]/base, no
    /// trailing slash), matching what sdk-python sends.</summary>
    internal static string ProofEndpoint(Uri baseAddress) =>
        baseAddress.GetLeftPart(UriPartial.Path).TrimEnd('/');

    internal static async Task<Guid?> TryAcquireAsync(HttpClient http, KaironOptions options,
        string service, string environment, byte[] body, CancellationToken ct, int? listenPort = null)
    {
        if (options.ProjectId == Guid.Empty || string.IsNullOrWhiteSpace(options.ApiKey) ||
            !KaironEndpointSecurity.IsAllowed(http.BaseAddress)) return null;
        var endpoint = ProofEndpoint(http.BaseAddress!);
        var port = listenPort ?? DefaultPort;
        if (!await ExchangeAsync(endpoint, null, ct, port)) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/telemetry/machine-proofs")
            {
                Content = JsonContent.Create(new
                {
                    projectId = options.ProjectId,
                    service,
                    environment,
                    bodySha256 = Convert.ToHexString(SHA256.HashData(body))
                })
            };
            request.Headers.TryAddWithoutValidation("X-Kairon-API-Key", options.ApiKey);
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            if (!document.RootElement.TryGetProperty("proofId", out var value) ||
                !Guid.TryParse(value.GetString(), out var id) || id == Guid.Empty) return null;
            return await ExchangeAsync(endpoint, id, ct, port) ? id : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> ExchangeAsync(string endpoint, Guid? proofId, CancellationToken ct, int port)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(proofId.HasValue ? 3 : 1));
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", port, timeout.Token);
            var stream = client.GetStream();
            var frame = JsonSerializer.SerializeToUtf8Bytes(new
            {
                endpoint,
                proofId = proofId?.ToString(),
                ping = !proofId.HasValue
            });
            if (frame.Length > 1024) return false;
            var prefix = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(prefix, frame.Length);
            await stream.WriteAsync(prefix, timeout.Token);
            await stream.WriteAsync(frame, timeout.Token);
            await stream.ReadExactlyAsync(prefix, timeout.Token);
            var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
            if (length is <= 0 or > 1024) return false;
            var reply = new byte[length];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            using var document = JsonDocument.Parse(reply);
            return document.RootElement.TryGetProperty("confirmed", out var result) && result.GetBoolean();
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or JsonException)
        {
            return false;
        }
    }
}
