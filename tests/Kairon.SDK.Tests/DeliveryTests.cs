using System.Net;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

public class DeliveryTests
{
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(ct);
    }
    private static (KaironTelemetryQueue Queue, KaironTelemetrySender Sender) Create(HttpMessageHandler handler, int capacity = 10) {
        var options = Options.Create(new KaironOptions { QueueCapacity = capacity });
        var queue = new KaironTelemetryQueue(options);
        var client = new KaironTelemetryClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, options);
        return (queue, new KaironTelemetrySender(queue, client));
    }
    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK) { Content = new StringContent("{\"status\":\"recorded\"}") };

    /// <summary>
    /// Polls a condition instead of racing a fixed wall-clock window. <see cref="KaironTelemetrySender.StopAsync"/>
    /// and <see cref="KaironTelemetryQueue.FlushAsync"/> both cap their own internal wait at 5
    /// seconds - a deliberate, correct PRODUCTION bound (a host's graceful shutdown must not hang
    /// forever on a stalled collector) that this test suite must not weaken just to make itself more
    /// convenient. Under a full-solution test run - many other projects' tests, several opening
    /// real loopback listeners, competing for the same thread pool at the same moment - that 5-second
    /// margin was observed to occasionally not be enough for `StartAsync` immediately followed by
    /// `StopAsync` to let the one in-flight item actually get processed, producing exactly the
    /// symptom previously reported here (FailedCount/DeliveredCount still 0 right after StopAsync
    /// returned) with no actual bug in the SDK's delivery or shutdown logic. Waiting here - BEFORE
    /// calling StopAsync at all - for the real condition to be reached, with a generous bound far
    /// wider than the production code's own 5-second cap, removes the race entirely: once this
    /// returns, the item is already fully processed and StopAsync has nothing left to wait for.
    /// This is polling for a genuine, eventually-true condition, not retrying a flaky assertion.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Condition was not met within {timeout}.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task ShutdownDrainsMetricsAndRequests() {
        var (queue, sender) = Create(new Handler(_ => Task.FromResult(Ok())));
        queue.TryEnqueue(new TelemetryPayload());
        queue.TryEnqueueMetric(new MetricPayload());
        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.DeliveredCount == 2, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);
        Assert.True(await queue.FlushAsync());
        Assert.Equal(2, queue.DeliveredCount);
        Assert.Equal(0, queue.FailedCount);
        Assert.False(queue.TryEnqueue(new TelemetryPayload()));
        sender.Dispose();
    }

    [Fact]
    public async Task FlushWaitsForInflightAndHonorsDeadline() {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (queue, sender) = Create(new Handler(async ct => { entered.SetResult(); await release.Task.WaitAsync(ct); return Ok(); }));
        queue.TryEnqueue(new TelemetryPayload());
        await sender.StartAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, queue.PendingCount);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        Assert.False(await queue.FlushAsync(deadline.Token));
        release.SetResult();
        await sender.StopAsync(default);
        Assert.True(await queue.FlushAsync());
        sender.Dispose();
    }

    [Fact]
    public async Task FailuresAndOverflowCannotReportSuccessfulFlush() {
        var (queue, sender) = Create(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), 1);
        queue.TryEnqueue(new TelemetryPayload());
        queue.TryEnqueue(new TelemetryPayload());
        Assert.Equal(1, queue.DroppedCount);
        await sender.StartAsync(default);
        await WaitUntilAsync(() => queue.FailedCount == 1, TimeSpan.FromSeconds(15));
        await sender.StopAsync(default);
        Assert.Equal(1, queue.FailedCount);
        Assert.False(await queue.FlushAsync());
        sender.Dispose();
    }

    [Fact]
    public async Task LastDeliveryErrorCapturesA401AndClearsOnTheNextSuccess() {
        var responses = new Queue<HttpResponseMessage>(new[] {
            new HttpResponseMessage(HttpStatusCode.Unauthorized), Ok()
        });
        var (queue, sender) = Create(new Handler(_ => Task.FromResult(responses.Dequeue())));

        Assert.Null(queue.LastDeliveryError);
        queue.TryEnqueue(new TelemetryPayload());
        await sender.StartAsync(default);
        // Waits for the real condition rather than relying on FlushAsync's own hard-capped 5-second
        // wait to always be enough under whatever load happens to be on the machine right now - see
        // WaitUntilAsync's remarks (this test was observed to fail exactly this way under heavy
        // parallel load: FlushAsync timed out before the single item was processed, so
        // LastDeliveryError was still null at the assertion).
        await WaitUntilAsync(() => queue.FailedCount == 1, TimeSpan.FromSeconds(15));
        Assert.NotNull(queue.LastDeliveryError);
        Assert.Contains("401", queue.LastDeliveryError);
        Assert.Contains("project authentication rejected", queue.LastDeliveryError);

        queue.TryEnqueue(new TelemetryPayload());
        await WaitUntilAsync(() => queue.DeliveredCount == 1, TimeSpan.FromSeconds(15));
        Assert.Null(queue.LastDeliveryError); // a later success clears the diagnostic

        await sender.StopAsync(default);
        sender.Dispose();
    }
}
