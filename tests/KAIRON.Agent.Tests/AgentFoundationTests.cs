using KAIRON.Agent;
using Microsoft.Extensions.Options;
using Xunit;

namespace KAIRON.Agent.Tests;

public sealed class AgentFoundationTests
{
    [Fact]
    public async Task MachineIdentityIsGeneratedOnceAndSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "kairon-agent-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "agent-identity.json");
        try
        {
            var store = new MachineIdentityStore(Options.Create(new AgentOptions { IdentityPath = path }));
            var first = await store.LoadOrCreateAsync(default);
            var restarted = await new MachineIdentityStore(Options.Create(new AgentOptions { IdentityPath = path }))
                .LoadOrCreateAsync(default);

            Assert.NotEqual(Guid.Empty, first.MachineId);
            Assert.Equal(first, restarted);
            Assert.True(first.AgentKey.Length >= 64);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ProcessCollectionIsBoundedAndReturnsSafeResourceValues()
    {
        var collector = new ProcessCollector(Options.Create(new AgentOptions { MaxProcesses = 3 }));
        var snapshots = collector.Collect();

        Assert.InRange(snapshots.Count, 1, 3);
        Assert.All(snapshots, process =>
        {
            Assert.True(process.ProcessId > 0);
            Assert.InRange(process.CpuPercent, 0, 100);
            Assert.True(process.MemoryBytes >= 0);
        });
    }
}
