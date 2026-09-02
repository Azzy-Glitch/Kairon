using Kairon.Backend.Configuration;
using Kairon.Backend.Services.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

public class IncidentProcessingQueueTests
{
    [Fact]
    public void DuplicateDetectionForTheSameScopeIsCoalesced()
    {
        var queue = CreateQueue();
        var item = DetectionItem();

        Assert.True(queue.TryEnqueue(item));
        Assert.True(queue.TryEnqueue(item with { Environment = " demo ", Service = "orders" }));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void InitialAndManualInvestigationForTheSameIncidentShareOneLease()
    {
        var queue = CreateQueue();
        var incidentId = Guid.NewGuid();
        var initial = DetectionItem() with
        {
            Kind = WorkItemKind.ProcessIncident,
            IncidentId = incidentId
        };
        var manual = initial with { Kind = WorkItemKind.ReinvestigateIncident };

        Assert.True(queue.TryEnqueue(initial));
        Assert.True(queue.TryEnqueue(manual));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task CompletionAllowsADeliberateLaterRequest()
    {
        var queue = CreateQueue();
        var item = DetectionItem() with
        {
            Kind = WorkItemKind.ProcessIncident,
            IncidentId = Guid.NewGuid()
        };

        Assert.True(queue.TryEnqueue(item));

        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        queue.Complete(reader.Current);

        Assert.True(queue.TryEnqueue(item));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public async Task AFullQueueRejectsWithoutPermanentlyLeasingTheRejectedItem()
    {
        var queue = CreateQueue(capacity: 1);
        var first = DetectionItem();
        var rejected = DetectionItem() with { ProjectId = Guid.NewGuid() };

        Assert.True(queue.TryEnqueue(first));
        Assert.False(queue.TryEnqueue(rejected));

        await using var reader = queue.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await reader.MoveNextAsync());
        queue.Complete(reader.Current);

        Assert.True(queue.TryEnqueue(rejected));
    }

    private static IncidentProcessingQueue CreateQueue(int capacity = 8) =>
        new(
            TestHarness.Opt(new AiOrchestrationOptions { QueueCapacity = capacity }),
            NullLogger<IncidentProcessingQueue>.Instance);

    private static IncidentWorkItem DetectionItem() => new(
        WorkItemKind.EvaluateDetection,
        Guid.Parse("550e8400-e29b-41d4-a716-446655440000"),
        "Demo",
        "Orders");
}
