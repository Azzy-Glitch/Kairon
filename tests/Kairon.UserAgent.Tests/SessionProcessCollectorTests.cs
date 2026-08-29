using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

public sealed class SessionProcessCollectorTests
{
    [Fact]
    public void OneInaccessibleProcessDoesNotStopCollectionOfTheOthers()
    {
        // Process A -> SUCCESS, Process B -> ACCESS DENIED, Process C -> SUCCESS (spec example).
        var source = new FakeProcessSnapshotSource(
            candidateIds: [1, 2, 3],
            reader: pid => pid == 2
                ? throw new UnauthorizedAccessException("Access is denied.")
                : new RawProcessSample(pid, DateTime.UtcNow, $"proc{pid}", "", TimeSpan.Zero, 1024));

        var results = SessionProcessCollector.CollectSamples(source, logger: null);

        Assert.Equal(2, results.Count);
        Assert.Contains(results, r => r.ProcessId == 1);
        Assert.Contains(results, r => r.ProcessId == 3);
        Assert.DoesNotContain(results, r => r.ProcessId == 2);
    }

    [Fact]
    public void AllProcessesFailingStillReturnsAnEmptyListNotAThrow()
    {
        var source = new FakeProcessSnapshotSource([1, 2],
            _ => throw new InvalidOperationException("gone"));

        var results = SessionProcessCollector.CollectSamples(source, logger: null);

        Assert.Empty(results);
    }

    [Fact]
    public void DeriveMachineIdMatchesKaironAgentsCopyForTheSameHostname()
    {
        // Guards the one piece of logic intentionally duplicated (not shared) between
        // Kairon.Agent and Kairon.UserAgent - both must land on the same Machine.
        var expected = ReferenceDeriveMachineId("DESKTOP-EXAMPLE");
        var actual = SessionProcessCollector.DeriveMachineId("DESKTOP-EXAMPLE");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DeriveMachineIdIsCaseInsensitiveOnHostname()
    {
        Assert.Equal(
            SessionProcessCollector.DeriveMachineId("host-a"),
            SessionProcessCollector.DeriveMachineId("HOST-A"));
    }

    /// <summary>Byte-for-byte copy of agent/Kairon.Agent/MachineRegistrationService.cs's
    /// DeriveMachineId, kept here only as an independent reference to compare against - this test
    /// project cannot reference Kairon.Agent's internal method directly.</summary>
    private static Guid ReferenceDeriveMachineId(string hostName)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(hostName.ToUpperInvariant()));
        return new Guid(hash.AsSpan(0, 16));
    }

    private sealed class FakeProcessSnapshotSource(IReadOnlyList<int> candidateIds,
        Func<int, RawProcessSample> reader) : IProcessSnapshotSource
    {
        public IReadOnlyList<int> GetCandidateProcessIds() => candidateIds;
        public RawProcessSample ReadSample(int processId) => reader(processId);
    }
}
