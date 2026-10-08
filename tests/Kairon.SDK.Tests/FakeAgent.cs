using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Kairon.SDK.Tests;

/// <summary>
/// A loopback stand-in for Kairon.Agent's proof listener on an ephemeral port: accepts any number
/// of length-prefixed frames, records them, and answers every one with "confirmed". Lets proof
/// tests run without ever touching the real Agent port (see TestAssemblyHermeticity).
/// </summary>
internal sealed class FakeAgent : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();

    public FakeAgent()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    /// <summary>Every frame's "endpoint" value, in arrival order.</summary>
    public ConcurrentQueue<string?> Endpoints { get; } = new();

    public int Frames => Endpoints.Count;

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                try
                {
                    var stream = client.GetStream();
                    var prefix = new byte[4];
                    await stream.ReadExactlyAsync(prefix, _cts.Token);
                    var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
                    if (length is <= 0 or > 1024) continue;
                    var frame = new byte[length];
                    await stream.ReadExactlyAsync(frame, _cts.Token);
                    using (var document = JsonDocument.Parse(frame))
                        Endpoints.Enqueue(document.RootElement.GetProperty("endpoint").GetString());
                    var reply = JsonSerializer.SerializeToUtf8Bytes(new { confirmed = true });
                    BinaryPrimitives.WriteInt32BigEndian(prefix, reply.Length);
                    await stream.WriteAsync(prefix, _cts.Token);
                    await stream.WriteAsync(reply, _cts.Token);
                }
                catch (IOException) { }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
    }
}
