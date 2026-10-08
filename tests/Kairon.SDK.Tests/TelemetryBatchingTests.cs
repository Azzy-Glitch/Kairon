using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// The sender drains the queue in batches rather than one POST per event: the backend's telemetry
/// rate limit counts requests (600/minute/IP), so per-event sends throttled any busy host. Batches
/// stay within the backend's bounds, keep idempotent EventIds across resends, and back off on 429.
/// </summary>
public sealed class TelemetryBatchingTests
{
    [Fact]
    public async Task QueuedTelemetryIsSentInBatchesOfAtMost25Events()
    {
        var server = new BatchServer();
        var (queue, sender) = Create(server);
        for (var i = 0; i < 60; i++) queue.TryEnqueueMetric(new MetricPayload { CpuPercent = i });

        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.DeliveredCount == 60, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);

        Assert.All(server.BatchSizes, size => Assert.InRange(size, 1, KaironTelemetrySender.MaxBatchEvents));
        Assert.Equal(new[] { 25, 25, 10 }, server.BatchSizes);
        Assert.Equal(60, server.EventIds.Distinct().Count());
        Assert.Equal(0, queue.FailedCount);
        sender.Dispose();
    }

    [Fact]
    public async Task ADrainedBatchIsSplitByScopeSoEveryPostCouldCarryOneMachineProof()
    {
        var server = new BatchServer();
        var (queue, sender) = Create(server);
        for (var i = 0; i < 6; i++)
            queue.TryEnqueueMetric(new MetricPayload
            {
                Application = "App", Service = i % 2 == 0 ? "Orders" : "Billing", Environment = "Production"
            });

        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.DeliveredCount == 6, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);

        Assert.Equal(2, server.BatchSizes.Count);
        Assert.All(server.BatchServices, services => Assert.Single(services.Distinct()));
        sender.Dispose();
    }

    [Fact]
    public async Task A429BacksOffForRetryAfterThenResendsTheSameEventIds()
    {
        var server = new BatchServer();
        server.Script.Enqueue(() =>
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
            return limited;
        });
        var (queue, sender) = Create(server);
        for (var i = 0; i < 3; i++) queue.TryEnqueueMetric(new MetricPayload { CpuPercent = i });

        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.DeliveredCount == 3, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);

        Assert.Equal(2, server.Posts.Count); // no immediate retry inside the same window
        Assert.True(server.Posts[1].At - server.Posts[0].At >= TimeSpan.FromMilliseconds(900),
            $"resent after only {(server.Posts[1].At - server.Posts[0].At).TotalMilliseconds}ms");
        Assert.Equal(server.Posts[0].EventIds, server.Posts[1].EventIds);
        Assert.Equal(0, queue.FailedCount);
        sender.Dispose();
    }

    [Fact]
    public async Task ServerRequestedBackoffIsBounded()
    {
        var server = new BatchServer();
        server.Script.Enqueue(() =>
        {
            var limited = new HttpResponseMessage((HttpStatusCode)429);
            limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return limited;
        });
        var client = Client(server);

        var result = await client.SendNormalizedBatchAsync(
            new[] { NormalizedTelemetryEvent.From(new MetricPayload(), client.Options) }, CancellationToken.None);

        Assert.True(result.RateLimited);
        Assert.Equal(KaironTelemetryClient.MaxRetryAfter, result.RetryAfter);
        Assert.Single(server.Posts);
        Assert.Equal(1, result.Failed);
    }

    [Fact]
    public async Task ShutdownDrainStaysBoundedWhileRateLimited()
    {
        var server = new BatchServer { AlwaysRateLimit = true };
        var (queue, sender) = Create(server);
        queue.TryEnqueueMetric(new MetricPayload());

        await sender.StartAsync(default);
        await WaitUntilAsync(() => server.Posts.Count == 1, TimeSpan.FromSeconds(15));
        var watch = Stopwatch.StartNew();
        await sender.StopAsync(default);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(8), $"stop took {watch.Elapsed}");
        await WaitUntilAsync(() => queue.FailedCount == 1, TimeSpan.FromSeconds(5));
        Assert.False(await queue.FlushAsync());
        sender.Dispose();
    }

    [Fact]
    public async Task AnOversizedBatchIsSplitUntilEachPostFitsTheBackendLimit()
    {
        var server = new BatchServer();
        var client = Client(server);
        var large = new string('x', 200_000);
        var events = Enumerable.Range(0, 10)
            .Select(_ => new NormalizedTelemetryEvent { ProjectId = client.Options.ProjectId, Service = "S", Message = large })
            .ToList();

        var result = await client.SendNormalizedBatchAsync(events, CancellationToken.None);

        Assert.Equal(10, result.Delivered);
        Assert.True(server.Posts.Count > 1);
        Assert.All(server.Posts, post => Assert.True(post.Bytes <= KaironTelemetryClient.MaxBatchBytes));
        Assert.Equal(10, server.EventIds.Distinct().Count());
    }

    [Fact]
    public async Task ASingleEventOverTheLimitFailsWithoutBeingSent()
    {
        var server = new BatchServer();
        var client = Client(server);

        var result = await client.SendNormalizedBatchAsync(new[]
        {
            new NormalizedTelemetryEvent { ProjectId = client.Options.ProjectId, Message = new string('x', 1_100_000) }
        }, CancellationToken.None);

        Assert.Equal(0, result.Delivered);
        Assert.Equal(1, result.Failed);
        Assert.Contains("size limit", result.Message);
        Assert.Empty(server.Posts);
    }

    [Fact]
    public async Task PartialRejectionIsCountedPerEvent()
    {
        var server = new BatchServer { Rejected = 1 };
        var (queue, sender) = Create(server);
        for (var i = 0; i < 4; i++) queue.TryEnqueueMetric(new MetricPayload());

        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.DeliveredCount + queue.FailedCount == 4, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);

        Assert.Equal(3, queue.DeliveredCount);
        Assert.Equal(1, queue.FailedCount);
        Assert.Equal("Collector rejected telemetry.", queue.LastDeliveryError);
        sender.Dispose();
    }

    private static KaironTelemetryClient Client(BatchServer server) =>
        new(new HttpClient(server) { BaseAddress = new Uri("http://localhost:8000/") },
            Options.Create(new KaironOptions { ProjectId = Guid.NewGuid(), ApiKey = "krn_key", TimeoutSeconds = 5 }));

    private static (KaironTelemetryQueue Queue, KaironTelemetrySender Sender) Create(BatchServer server)
    {
        var client = Client(server);
        var queue = new KaironTelemetryQueue(Options.Create(client.Options));
        return (queue, new KaironTelemetrySender(queue, client));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException($"Condition was not met within {timeout}.");
            await Task.Delay(10);
        }
    }

    private sealed record Post(DateTime At, int Bytes, Guid[] EventIds);

    /// <summary>Answers like the backend: counts what was POSTed, unless scripted otherwise.</summary>
    private sealed class BatchServer : HttpMessageHandler
    {
        private readonly object _gate = new();
        public List<Post> Posts { get; } = new();
        public List<int> BatchSizes { get; } = new();
        public List<string[]> BatchServices { get; } = new();
        public IEnumerable<Guid> EventIds => Posts.SelectMany(p => p.EventIds);
        public Queue<Func<HttpResponseMessage>> Script { get; } = new();
        public bool AlwaysRateLimit { get; init; }
        public int Rejected { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("/api/v1/telemetry/events", request.RequestUri!.AbsolutePath);
            var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
            using var body = JsonDocument.Parse(bytes);
            var events = body.RootElement.GetProperty("events").EnumerateArray().ToArray();
            lock (_gate)
            {
                Posts.Add(new Post(DateTime.UtcNow, bytes.Length,
                    events.Select(e => e.GetProperty("eventId").GetGuid()).ToArray()));
                BatchSizes.Add(events.Length);
                BatchServices.Add(events.Select(e => e.GetProperty("service").GetString()!).ToArray());
                if (Script.TryDequeue(out var scripted)) return scripted();
            }
            if (AlwaysRateLimit)
            {
                var limited = new HttpResponseMessage((HttpStatusCode)429);
                limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
                return limited;
            }
            var accepted = Math.Max(0, events.Length - Rejected);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $"{{\"accepted\":{accepted},\"duplicates\":0,\"rejected\":{events.Length - accepted}}}")
            };
        }
    }
}
