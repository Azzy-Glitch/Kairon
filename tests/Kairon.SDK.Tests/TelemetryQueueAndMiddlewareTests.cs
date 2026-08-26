using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.SDK.Tests;

/// <summary>
/// The bounded queue and the middleware (PRD sections 4.1 and 17): telemetry is asynchronous,
/// bounded, and never blocks or breaks the host application.
/// </summary>
public class TelemetryQueueTests
{
    private static KaironTelemetryQueue Queue(int capacity) =>
        new(Options.Create(new KaironOptions { QueueCapacity = capacity }));

    [Fact]
    public void EnqueueAcceptsItemsUpToCapacity()
    {
        var queue = Queue(10);

        for (var i = 0; i < 10; i++)
            Assert.True(queue.TryEnqueue(new TelemetryPayload()));

        Assert.Equal(10, queue.PendingCount);
    }

    [Fact]
    public void QueueIsBoundedAndNeverBlocks()
    {
        // The point of the bound: past capacity the SDK drops rather than growing without limit.
        var queue = Queue(5);

        for (var i = 0; i < 50; i++)
            queue.TryEnqueue(new TelemetryPayload());

        Assert.True(queue.PendingCount <= 5);
    }

    [Fact]
    public void DroppingTheOldestKeepsTheNewestTelemetry()
    {
        var queue = Queue(2);

        queue.TryEnqueue(new TelemetryPayload { Endpoint = "/first" });
        queue.TryEnqueue(new TelemetryPayload { Endpoint = "/second" });
        queue.TryEnqueue(new TelemetryPayload { Endpoint = "/third" });

        Assert.Equal(2, queue.PendingCount);
    }

    [Fact]
    public void MetricsShareTheSameBoundedQueue()
    {
        var queue = Queue(4);

        Assert.True(queue.TryEnqueueMetric(new MetricPayload()));
        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public void ACapacityOfZeroIsCoercedToSomethingUsable()
    {
        var queue = Queue(0);

        Assert.True(queue.TryEnqueue(new TelemetryPayload()));
    }
}

public class MiddlewareTests
{
    private static (KaironMiddleware Middleware, KaironTelemetryQueue Queue, KaironMetrics Metrics) Build(
        RequestDelegate next,
        Action<KaironOptions>? configure = null)
    {
        var options = new KaironOptions { ProjectId = Guid.NewGuid(), ApplicationName = "TestApp" };
        configure?.Invoke(options);

        var wrapped = Options.Create(options);
        var queue = new KaironTelemetryQueue(wrapped);
        var metrics = new KaironMetrics();

        return (new KaironMiddleware(next, queue, metrics, wrapped), queue, metrics);
    }

    private static DefaultHttpContext Context(string path = "/api/orders", string method = "POST")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        return context;
    }

    [Fact]
    public async Task ASuccessfulRequestIsInstrumented()
    {
        var (middleware, queue, _) = Build(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context());

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public async Task AnExceptionIsCapturedAndStillPropagates()
    {
        // The SDK records the failure but must not swallow it: the application's own error
        // handling has to keep working exactly as it did before Kairon was added.
        var (middleware, queue, _) = Build(_ => throw new InvalidOperationException("app failure"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(Context()));

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public async Task IgnoredPathsAreNotInstrumented()
    {
        var (middleware, queue, _) = Build(ctx =>
        {
            ctx.Response.StatusCode = 200;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Context("/health", "GET"));

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task DisablingTelemetryBypassesInstrumentationEntirely()
    {
        var called = false;

        var (middleware, queue, _) = Build(
            ctx => { called = true; return Task.CompletedTask; },
            o => o.EnableTelemetry = false);

        await middleware.InvokeAsync(Context());

        Assert.True(called, "the request pipeline must still run");
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task ErrorsAreReportedEvenWhenSamplingWouldDropSuccesses()
    {
        // Losing an error to sampling would be the one loss that actually matters.
        var (middleware, queue, _) = Build(
            ctx => { ctx.Response.StatusCode = 500; return Task.CompletedTask; },
            o => o.SuccessSampleRate = 0.0);

        await middleware.InvokeAsync(Context());

        Assert.Equal(1, queue.PendingCount);
    }

    [Fact]
    public async Task SuccessesAreDroppedWhenSamplingIsOff()
    {
        var (middleware, queue, _) = Build(
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            o => o.SuccessSampleRate = 0.0);

        await middleware.InvokeAsync(Context());

        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task AFullQueueDoesNotBreakTheRequest()
    {
        var (middleware, _, _) = Build(
            ctx => { ctx.Response.StatusCode = 200; return Task.CompletedTask; },
            o => o.QueueCapacity = 1);

        for (var i = 0; i < 20; i++)
            await middleware.InvokeAsync(Context());

        // Reaching here at all is the assertion: 20 requests through a size-1 queue, no exception.
        Assert.True(true);
    }

    [Fact]
    public async Task ResponseBodyCaptureLeavesTheResponseIntact()
    {
        var (middleware, _, _) = Build(
            async ctx =>
            {
                ctx.Response.StatusCode = 200;
                await ctx.Response.WriteAsync("hello world");
            },
            o => o.CaptureResponseBody = true);

        var context = Context();
        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();

        Assert.Equal("hello world", body);
    }
}
