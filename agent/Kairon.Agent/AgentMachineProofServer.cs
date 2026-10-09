using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Kairon.Agent;

/// <summary>
/// Local proof bridge. Only an opaque challenge id crosses the localhost boundary; no SDK API
/// key is ever sent to this unauthenticated listener. The Agent confirms the challenge directly
/// with its configured backend using its own enrolled-machine key, never revealing that key to
/// the SDK. A copied SDK credential on another host cannot call this machine's loopback listener.
/// </summary>
public sealed class AgentMachineProofServer : BackgroundService
{
    public const int Port = 47891;
    private readonly int _port;
    private readonly HttpClient _http;
    private readonly AgentOptions _options;
    private readonly MachineRegistrationService _registration;
    private readonly ILogger<AgentMachineProofServer> _logger;

    public AgentMachineProofServer(HttpClient http, IOptions<AgentOptions> options,
        MachineRegistrationService registration, ILogger<AgentMachineProofServer> logger,
        int? listenPort = null)
    {
        _http = http;
        _options = options.Value;
        _registration = registration;
        _logger = logger;
        _port = listenPort ?? Port;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.EnableMachineRegistration) return;
        using var slots = new SemaphoreSlim(8);
        var listener = new TcpListener(IPAddress.Loopback, _port);
        try { listener.Start(16); }
        catch (SocketException)
        {
            _logger.LogWarning("kairon-agent: local machine-proof listener unavailable (port already in use)");
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await slots.WaitAsync(stoppingToken);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stoppingToken); }
                catch { slots.Release(); throw; }
                _ = HandleAsync(client, slots, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { listener.Stop(); }
    }

    private async Task HandleAsync(TcpClient client, SemaphoreSlim slots, CancellationToken stoppingToken)
    {
        using (client)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, _options.TimeoutSeconds + 2)));
                var stream = client.GetStream();
                var input = JsonSerializer.Deserialize<ProofRequest>(await ReadFrameAsync(stream, deadline.Token),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web));
                var matchingBackend = input is not null && SameBackend(input.Endpoint, _options.Endpoint) &&
                    AgentEndpointSecurity.IsAllowed(_http.BaseAddress);
                var accepted = false;
                if (matchingBackend && input!.Ping)
                {
                    accepted = true;
                }
                else if (matchingBackend && Guid.TryParse(input!.ProofId, out var proofId) && proofId != Guid.Empty)
                {
                    // Which process sent this proof, according to Windows - not according to the
                    // caller. Lets KAIRON restart exactly that application process when approved.
                    var processId = LoopbackConnectionOwner.Find(client.Client.RemoteEndPoint as IPEndPoint, _port);
                    using var request = new HttpRequestMessage(HttpMethod.Post,
                        $"api/agent/machines/{_registration.MachineId}/telemetry-proofs/{proofId}/confirm")
                    {
                        Content = JsonContent.Create(new { processId })
                    };
                    request.Headers.TryAddWithoutValidation("X-Kairon-Agent-Key", _options.AgentKey);
                    using var response = await _http.SendAsync(request, deadline.Token);
                    accepted = response.IsSuccessStatusCode;
                }
                await WriteFrameAsync(stream, JsonSerializer.SerializeToUtf8Bytes(new ProofResponse(accepted),
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)), deadline.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Neither the Agent secret, backend address nor challenge is logged.
                _logger.LogDebug("kairon-agent: machine-proof exchange failed ({Type})", ex.GetType().Name);
            }
            finally { slots.Release(); }
        }
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken ct)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, ct);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > 1024) throw new InvalidDataException("Machine-proof frame limit exceeded.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct);
        return bytes;
    }

    private static async Task WriteFrameAsync(NetworkStream stream, byte[] bytes, CancellationToken ct)
    {
        var prefix = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct);
        await stream.WriteAsync(bytes, ct);
    }

    private sealed record ProofRequest(string? Endpoint, string? ProofId, bool Ping);
    private sealed record ProofResponse(bool Confirmed);

    private static bool SameBackend(string? requested, string configured)
    {
        if (!Uri.TryCreate(requested, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(configured, UriKind.Absolute, out var b)) return false;
        if (a.Scheme != b.Scheme || a.Port != b.Port || a.UserInfo.Length != 0 || b.UserInfo.Length != 0 ||
            a.AbsolutePath.TrimEnd('/') != b.AbsolutePath.TrimEnd('/') ||
            a.Query.Length != 0 || b.Query.Length != 0 || a.Fragment.Length != 0 || b.Fragment.Length != 0)
            return false;
        // localhost and 127.0.0.1 are the same installed loopback backend, but arbitrary remote
        // names must match exactly. Never let a caller redirect Agent-key traffic to a URL it chose.
        static bool Loopback(string host) => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase);
        return a.IdnHost.Equals(b.IdnHost, StringComparison.OrdinalIgnoreCase) ||
            (Loopback(a.IdnHost) && Loopback(b.IdnHost));
    }
}
