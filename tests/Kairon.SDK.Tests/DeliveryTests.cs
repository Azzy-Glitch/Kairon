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

    [Fact]
    public async Task ShutdownDrainsMetricsAndRequests() {
        var (queue, sender) = Create(new Handler(_ => Task.FromResult(Ok())));
        queue.TryEnqueue(new TelemetryPayload());
        queue.TryEnqueueMetric(new MetricPayload());
        await sender.StartAsync(default);
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
        await queue.FlushAsync();
        Assert.NotNull(queue.LastDeliveryError);
        Assert.Contains("401", queue.LastDeliveryError);
        Assert.Contains("project authentication rejected", queue.LastDeliveryError);

        queue.TryEnqueue(new TelemetryPayload());
        await queue.FlushAsync();
        Assert.Null(queue.LastDeliveryError); // a later success clears the diagnostic

        await sender.StopAsync(default);
        sender.Dispose();
    }
}
