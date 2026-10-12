using System.Text.Json;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Services;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The shared diagnosis contract (tests/fixtures/diagnosis-scenarios.json). The AI service's mock
/// provider runs the same file (ai-service/tests/test_diagnosis_contract.py), so the backend's and
/// the AI service's offline decision rules cannot drift apart unnoticed.
/// </summary>
public sealed class DiagnosisContractTests
{
    public static IEnumerable<object[]> Scenarios()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        foreach (var scenario in document.RootElement.GetProperty("scenarios").EnumerateArray())
            yield return [scenario.GetProperty("name").GetString()!];
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void TheBackendSatisfiesTheSharedScenario(string name)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath()));
        var scenario = document.RootElement.GetProperty("scenarios").EnumerateArray()
            .Single(s => s.GetProperty("name").GetString() == name);
        var evidence = scenario.GetProperty("evidence").Deserialize<EvidencePackageDto>()!;
        var expect = scenario.GetProperty("expect");

        var result = DeterministicInvestigation.Investigate(evidence);

        if (expect.TryGetProperty("certainty", out var certainty))
            Assert.Equal(certainty.GetString(), result.RootCauseCertainty);
        if (expect.TryGetProperty("recommends", out var recommends))
            Assert.Equal(recommends.EnumerateArray().Select(a => a.GetString()!).ToArray(), result.Recommendations.Select(r => r.Action).ToArray());
        foreach (var text in Strings(expect, "root_contains")) Assert.Contains(text, result.RootCause);
        foreach (var text in Strings(expect, "root_excludes")) Assert.DoesNotContain(text, result.RootCause);
        foreach (var text in Strings(expect, "alternatives_contain"))
            Assert.Contains(result.AlternativeExplanations, a => a.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var text in Strings(expect, "next_steps_contain"))
            Assert.Contains(result.NextSteps, s => s.Contains(text, StringComparison.OrdinalIgnoreCase));
        foreach (var text in Strings(expect, "evidence_excludes"))
            Assert.DoesNotContain(result.Evidence, e => e.Contains(text, StringComparison.OrdinalIgnoreCase));
        if (expect.TryGetProperty("restart_verdict", out var verdict))
            Assert.Equal(verdict.GetString(), result.ConsideredActions.Single(c => c.Action == "RestartApplication").Verdict);
    }

    private static IEnumerable<string> Strings(JsonElement expect, string name) =>
        expect.TryGetProperty(name, out var values) ? values.EnumerateArray().Select(v => v.GetString()!) : [];

    private static string FixturePath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "diagnosis-scenarios.json");
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException("tests/fixtures/diagnosis-scenarios.json not found above " + AppContext.BaseDirectory);
    }
}
