using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The frontend AI Configuration panel's backend surface: provider validation, save/apply,
/// Test Connection (with and without retyping an already-saved key), and model discovery.
/// </summary>
public sealed class AiConfigControllerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IAiProviderConfigService _configService;
    private readonly ScriptableAiMicroservice _ai;
    private readonly AiConfigController _controller;

    public AiConfigControllerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        _configService = new AiProviderConfigService(_db, new EphemeralDataProtectionProvider(), TimeProvider.System);
        _ai = new ScriptableAiMicroservice();
        _controller = new AiConfigController(_configService, _ai);
    }

    [Fact]
    public async Task UnsupportedProviderIsRejectedBeforeTouchingAnything()
    {
        var result = await _controller.Save(new SaveAiConfigRequest { Provider = "openai" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.Equal(0, _ai.ConfigureCalls);
        Assert.False(await _configService.HasValidConfigurationAsync());
    }

    [Theory]
    [InlineData("groq")]
    [InlineData("qwen")]
    [InlineData("gemini")]
    public async Task EachOfTheThreeSupportedProvidersCanBeSaved(string provider)
    {
        var result = await _controller.Save(
            new SaveAiConfigRequest { Provider = provider, ApiKey = "real-looking-key" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.True(await _configService.HasValidConfigurationAsync());
        Assert.Equal(1, _ai.ConfigureCalls);
    }

    [Fact]
    public async Task SaveAppliesLiveThroughTheAiMicroservice()
    {
        await _controller.Save(
            new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_x", Model = "openai/gpt-oss-120b" },
            CancellationToken.None);

        Assert.Equal("groq", _ai.LastConfigureRequest?.Provider);
        Assert.Equal("gsk_x", _ai.LastConfigureRequest?.ApiKey);
        Assert.Equal("openai/gpt-oss-120b", _ai.LastConfigureRequest?.Model);
    }

    [Fact]
    public async Task SaveResponseNeverContainsTheRawKey()
    {
        var result = await _controller.Save(
            new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_super_secret" }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain("gsk_super_secret", ok.Value!.ToString());
    }

    [Fact]
    public async Task SaveStillSucceedsWhenTheAiServiceIsUnreachable()
    {
        _ai.ThrowOnConfigure = new HttpRequestException("connection refused");

        var result = await _controller.Save(
            new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_x" }, CancellationToken.None);

        // The key must not be lost just because the AI service hasn't finished starting yet - the
        // startup sync (or the next Save/Test) applies it later.
        Assert.IsType<OkObjectResult>(result);
        Assert.True(await _configService.HasValidConfigurationAsync());
    }

    [Fact]
    public async Task TestConnectionWithoutRetypingAKeyUsesTheStoredOne()
    {
        await _controller.Save(new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_saved" }, CancellationToken.None);
        _ai.NextTestResult = new AiTestConnectionResponseDto { Success = true, Provider = "groq", EffectiveProvider = "groq", Model = "m" };

        var result = await _controller.Test(new SaveAiConfigRequest { Provider = "groq" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("gsk_saved", _ai.LastTestRequest?.ApiKey);
    }

    [Fact]
    public async Task TestConnectionForADifferentProviderThanWhatsStoredDoesNotLeakTheStoredKey()
    {
        await _controller.Save(new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_saved" }, CancellationToken.None);

        await _controller.Test(new SaveAiConfigRequest { Provider = "qwen" }, CancellationToken.None);

        Assert.Null(_ai.LastTestRequest?.ApiKey);
    }

    [Fact]
    public async Task ModelsForAnUnsupportedProviderIsRejected()
    {
        var result = await _controller.Models(new SaveAiConfigRequest { Provider = "openai" }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public async Task ModelsFallsBackToTheStoredKeyWhenNoneIsSupplied()
    {
        await _controller.Save(new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_saved" }, CancellationToken.None);
        _ai.NextModelsResult = new AiModelsResponseDto { Provider = "groq", Supported = true, Models = new() { "openai/gpt-oss-120b" } };

        var result = await _controller.Models(new SaveAiConfigRequest { Provider = "groq" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal("gsk_saved", _ai.LastModelsRequest?.ApiKey);
    }

    [Fact]
    public async Task SavePassesACustomEndpointThroughToTheAiMicroservice()
    {
        const string dedicatedEndpoint =
            "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions";

        var result = await _controller.Save(
            new SaveAiConfigRequest { Provider = "qwen", ApiKey = "sk-ws-x", Endpoint = dedicatedEndpoint },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(dedicatedEndpoint, _ai.LastConfigureRequest?.Endpoint);
    }

    [Fact]
    public async Task TestConnectionSendsTheRequestedEndpointVerbatimSoAClearedBoxTestsTheDefault()
    {
        // The endpoint field is authoritative for a test, unlike the API key: it is not a secret,
        // so the UI pre-fills it and the operator sees exactly what will be exercised. Falling back
        // to the stored endpoint here would silently test a URL the operator had just cleared.
        const string dedicatedEndpoint =
            "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions";
        await _controller.Save(
            new SaveAiConfigRequest { Provider = "qwen", ApiKey = "sk-ws-x", Endpoint = dedicatedEndpoint },
            CancellationToken.None);

        var result = await _controller.Test(
            new SaveAiConfigRequest { Provider = "qwen", Endpoint = "" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(string.Empty, _ai.LastTestRequest?.Endpoint);
    }

    [Fact]
    public async Task ClearingTheEndpointPushesTheClearedValueLiveNotTheStaleOne()
    {
        // The defect this covers: the row recorded "no override" while the running AI service kept
        // calling the old URL until the next restart, because a null/omitted endpoint reads as
        // "leave it alone". Save must forward what was actually stored.
        const string dedicatedEndpoint =
            "https://ws-8s7id56fv8yt5bmm.ap-southeast-1.maas.aliyuncs.com/compatible-mode/v1/chat/completions";
        await _controller.Save(
            new SaveAiConfigRequest { Provider = "qwen", ApiKey = "sk-ws-x", Endpoint = dedicatedEndpoint },
            CancellationToken.None);

        await _controller.Save(
            new SaveAiConfigRequest { Provider = "qwen", Endpoint = "" }, CancellationToken.None);

        Assert.Equal(string.Empty, _ai.LastConfigureRequest?.Endpoint);
        var stored = await _configService.GetAsync();
        Assert.Equal(string.Empty, stored.Endpoint);
    }

    [Fact]
    public async Task GetNeverReturnsAKeyEvenAfterASave()
    {
        await _controller.Save(new SaveAiConfigRequest { Provider = "groq", ApiKey = "gsk_super_secret" }, CancellationToken.None);

        var result = await _controller.Get(CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.DoesNotContain("gsk_super_secret", ok.Value!.ToString());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}

/// <summary>Scriptable double for IAiMicroservice, purpose-built for the AI Configuration
/// controller tests - distinct from TestHarness's FakeAiService, which scripts the SRE
/// investigation surface instead.</summary>
public sealed class ScriptableAiMicroservice : IAiMicroservice
{
    public bool IsAvailable { get; set; } = true;
    public int ConfigureCalls { get; private set; }
    public AiConfigureRequestDto? LastConfigureRequest { get; private set; }
    public AiConfigureRequestDto? LastTestRequest { get; private set; }
    public AiConfigureRequestDto? LastModelsRequest { get; private set; }
    public Exception? ThrowOnConfigure { get; set; }
    public AiTestConnectionResponseDto? NextTestResult { get; set; }
    public AiModelsResponseDto? NextModelsResult { get; set; }

    public Task<string> GetModeAsync(CancellationToken cancellationToken = default) => Task.FromResult("live");

    public Task<AiConfigureResponseDto> ConfigureProviderAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        ConfigureCalls++;
        LastConfigureRequest = request;
        if (ThrowOnConfigure is not null) throw ThrowOnConfigure;
        return Task.FromResult(new AiConfigureResponseDto
        {
            Applied = true, Provider = request.Provider, EffectiveProvider = request.Provider, Model = request.Model ?? ""
        });
    }

    public Task<AiTestConnectionResponseDto> TestProviderConnectionAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        LastTestRequest = request;
        return Task.FromResult(NextTestResult ?? new AiTestConnectionResponseDto
        {
            Success = true, Provider = request.Provider, EffectiveProvider = request.Provider, Model = request.Model ?? ""
        });
    }

    public Task<AiModelsResponseDto> ListProviderModelsAsync(AiConfigureRequestDto request, CancellationToken cancellationToken = default)
    {
        LastModelsRequest = request;
        return Task.FromResult(NextModelsResult ?? new AiModelsResponseDto { Provider = request.Provider, Supported = false });
    }

    public Task<Kairon.Backend.DTOs.ErrorAnalysisResponse> AnalyzeErrorAsync(string log, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.ErrorAnalysisResponse());

    public Task<Kairon.Backend.DTOs.PredictionResponse> PredictAsync(List<string> recentLogs, string currentLog, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.PredictionResponse());

    public Task<Kairon.Backend.DTOs.RecommendationResponse> RecommendAsync(string context, CancellationToken cancellationToken = default)
        => Task.FromResult(new Kairon.Backend.DTOs.RecommendationResponse());

    public Task<List<Kairon.Backend.DTOs.FixSuggestionDto>> SuggestFixesAsync(List<Kairon.Backend.DTOs.MismatchDto> mismatches, CancellationToken cancellationToken = default)
        => Task.FromResult(new List<Kairon.Backend.DTOs.FixSuggestionDto>());

    public Task<Kairon.Backend.DTOs.Sre.InvestigationResultDto> InvestigateAsync(Kairon.Backend.DTOs.Sre.EvidencePackageDto evidence, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Not exercised by the AI Configuration controller tests.");
}
