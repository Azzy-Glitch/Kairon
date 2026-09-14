using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Phase 3: processAlive, providerConfigured (AiMode) and providerReachable (AiProviderReachable)
/// are three distinct concepts that must never be conflated - a process that is alive but has no
/// usable provider must never be reported as unhealthy merely because AI isn't configured, and a
/// configured-but-currently-failing provider must be distinguishable from "not configured at all".
/// </summary>
public sealed class HealthStatusControllerTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private HealthStatusController Controller(IAiMicroservice ai) => new(
        _h.Db, ai,
        TestHarness.Opt(new DetectionOptions()),
        TestHarness.Opt(new RemediationOptions()),
        TestHarness.Opt(new PersistenceOptions()),
        new PersistenceMaintenanceState());

    private static async Task<SystemHealthDto> StatusAsync(HealthStatusController controller) =>
        (SystemHealthDto)Assert.IsType<OkObjectResult>(await controller.Status(default)).Value!;

    [Fact]
    public async Task ProcessAliveWithNoProviderConfiguredIsReportedAsUnconfiguredNotUnhealthy()
    {
        var body = await StatusAsync(Controller(new FakeAi(mode: "unconfigured", isAvailable: true)));

        Assert.True(body.AiService, "the process itself is alive and must not be reported as down");
        Assert.Equal("unconfigured", body.AiMode);
    }

    [Fact]
    public async Task ProcessAliveWithExplicitMockIsReportedAsMock()
    {
        var body = await StatusAsync(Controller(new FakeAi(mode: "mock", isAvailable: true)));

        Assert.True(body.AiService);
        Assert.Equal("mock", body.AiMode);
        Assert.True(body.AiProviderReachable);
    }

    [Fact]
    public async Task ProcessAliveWithAConfiguredReachableProviderIsReportedAsLive()
    {
        var body = await StatusAsync(Controller(new FakeAi(mode: "groq", isAvailable: true)));

        Assert.True(body.AiService);
        Assert.Equal("groq", body.AiMode);
        Assert.True(body.AiProviderReachable);
    }

    [Fact]
    public async Task ProcessAliveWithAConfiguredButUnreachableProviderIsDistinguishedFromUnconfigured()
    {
        // A real outage: the provider IS configured (AiMode names it) but the circuit breaker has
        // observed real failures - this must never be reported the same way as "no provider
        // configured at all".
        var body = await StatusAsync(Controller(new FakeAi(mode: "groq", isAvailable: false)));

        Assert.True(body.AiService, "the AI process itself is still alive");
        Assert.Equal("groq", body.AiMode);
        Assert.False(body.AiProviderReachable);
    }

    [Fact]
    public async Task ProcessUnavailableIsReportedAsBackendDownNotMerelyUnconfigured()
    {
        var body = await StatusAsync(Controller(new FakeAi(mode: "unavailable", isAvailable: false)));

        Assert.False(body.AiService);
        Assert.Equal("unavailable", body.AiMode);
    }

    private sealed class FakeAi : IAiMicroservice
    {
        private readonly string _mode;

        public FakeAi(string mode, bool isAvailable)
        {
            _mode = mode;
            IsAvailable = isAvailable;
        }

        public bool IsAvailable { get; }
        public Task<string> GetModeAsync(CancellationToken cancellationToken = default) => Task.FromResult(_mode);

        public Task<ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<List<FixSuggestionDto>> SuggestFixesAsync(List<MismatchDto> mismatches, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<DTOs.Sre.InvestigationResultDto> InvestigateAsync(DTOs.Sre.EvidencePackageDto evidence, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<AiConfigureResponseDto> ConfigureProviderAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<AiTestConnectionResponseDto> TestProviderConnectionAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
        public Task<AiModelsResponseDto> ListProviderModelsAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
