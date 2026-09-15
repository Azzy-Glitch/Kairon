using System.Net;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The precedence rule frontend PRD section 13 asks for: a saved AI Configuration overrides
/// appsettings.json's static AiService:MockMode, but an untouched deployment (no saved row) keeps
/// exactly its existing env/appsettings.json-driven behaviour.
/// </summary>
public sealed class AiMicroserviceModeTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IAiProviderConfigService _configService;

    public AiMicroserviceModeTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _configService = new AiProviderConfigService(_db, new EphemeralDataProtectionProvider(), TimeProvider.System);
    }

    private AiMicroservice CreateMicroservice(bool staticMockMode, string? reportedMode = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiService:MockMode"] = staticMockMode.ToString() })
            .Build();

        return new AiMicroservice(
            reportedMode is null ? new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") } : new HttpClient(new ModeHandler(reportedMode)) { BaseAddress = new Uri("http://127.0.0.1:1") },
            configuration,
            Options.Create(new AiOrchestrationOptions()),
            _configService,
            NullLogger<AiMicroservice>.Instance);
    }

    private AiMicroservice CreateMicroserviceWithHandler(HttpMessageHandler handler, TimeProvider time)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiService:MockMode"] = "false" })
            .Build();

        return new AiMicroservice(
            new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:1") },
            configuration,
            Options.Create(new AiOrchestrationOptions()),
            _configService,
            NullLogger<AiMicroservice>.Instance,
            time);
    }

    [Fact]
    public async Task NoSavedConfigurationPreservesTheExistingStaticMockModeTrue()
    {
        var ai = CreateMicroservice(staticMockMode: true);

        Assert.Equal("mock", await ai.GetModeAsync());
        var result = await ai.AnalyzeErrorAsync("NullReferenceException");
        Assert.StartsWith("Mock:", result.RootCause);
    }

    [Fact]
    public async Task AnUnavailableExternalServiceIsNotReportedAsLive()
    {
        var ai = CreateMicroservice(staticMockMode: false);

        Assert.Equal("unavailable", await ai.GetModeAsync());
    }

    [Fact]
    public async Task ASavedConfigurationOverridesStaticMockModeTrue()
    {
        await _configService.SaveAsync("groq", "gsk_real_looking_key", "openai/gpt-oss-120b", null);
        var ai = CreateMicroservice(staticMockMode: true, reportedMode: "live");

        Assert.Equal("groq", await ai.GetModeAsync());
    }

    [Fact]
    public async Task ASavedConfigurationMeansAnalyzeErrorNoLongerReturnsTheHardcodedMock()
    {
        await _configService.SaveAsync("groq", "gsk_real_looking_key", "openai/gpt-oss-120b", null);
        var ai = CreateMicroservice(staticMockMode: true);

        // Nothing is listening on 127.0.0.1:1, so this proves a REAL call was attempted (and
        // failed) rather than silently returning to the mock helper - the meaningful behaviour
        // change, not just what GetModeAsync reports.
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("NullReferenceException"));
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ExternalMockIsReportedAsMockEvenWhenStaticMockIsDisabled()
    {
        Assert.Equal("mock", await CreateMicroservice(false, "mock").GetModeAsync());
    }

    /// <summary>The production blocker this fixes: the AI service process is up and answering
    /// (a real HTTP 200 from /health), but it has no usable credential for the provider it was
    /// asked to use (kairon.providers.UnconfiguredProvider reports mode="unconfigured", never
    /// "mock" - it never fabricates output). This must surface as its own distinct string, not be
    /// folded into "live" (which would misreport it as a real, working provider) or "unknown"
    /// (which would misreport it as an unparsable/unexpected response).</summary>
    [Fact]
    public async Task UnconfiguredExternalProviderIsReportedAsUnconfiguredNeverLiveOrUnknown()
    {
        Assert.Equal("unconfigured", await CreateMicroservice(false, "unconfigured").GetModeAsync());
    }

    private sealed class ModeHandler(string mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"mode\":\"" + mode + "\"}") });
    }

    // --- RB-007: IsAvailable ("AiProviderReachable") must never become true merely because a
    // cooldown window elapsed - only a real, successful call may do that. ---

    [Fact]
    public async Task AFailedCallImmediatelyReportsUnreachable()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ai = CreateMicroserviceWithHandler(new ScriptedHandler(HttpStatusCode.ServiceUnavailable), time);

        Assert.True(ai.IsAvailable); // optimistic default before any call
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom"));

        Assert.False(ai.IsAvailable);
    }

    [Fact]
    public async Task CooldownExpiringWithNoNewCallNeverFlipsReachableBackToTrue()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var ai = CreateMicroserviceWithHandler(new ScriptedHandler(HttpStatusCode.ServiceUnavailable), time);
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom"));
        Assert.False(ai.IsAvailable);

        // The cooldown window passes with no further call attempted at all - the bug this fixes
        // would have reported IsAvailable=true here purely from elapsed time.
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.False(ai.IsAvailable);
    }

    [Fact]
    public async Task ARetryAfterCooldownThatAlsoFailsStillReportsUnreachable()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.ServiceUnavailable);
        var ai = CreateMicroserviceWithHandler(handler, time);
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom"));
        time.Advance(TimeSpan.FromMinutes(5));

        // The half-open probe is allowed through (the handler IS invoked again)...
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom again"));

        Assert.Equal(2, handler.CallCount);
        // ...but since it failed too, reachability remains false - a real failed retry, not a
        // timer, is what this reflects.
        Assert.False(ai.IsAvailable);
    }

    [Fact]
    public async Task ARetryAfterCooldownThatSucceedsRestoresReachability()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        var ai = CreateMicroserviceWithHandler(handler, time);
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom"));
        Assert.False(ai.IsAvailable);
        time.Advance(TimeSpan.FromMinutes(5));

        await ai.AnalyzeErrorAsync("recovered now");

        Assert.True(ai.IsAvailable);
    }

    [Fact]
    public async Task BeforeCooldownElapsesANewCallIsBlockedWithoutEvenAttemptingTheNetwork()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var handler = new ScriptedHandler(HttpStatusCode.ServiceUnavailable);
        var ai = CreateMicroserviceWithHandler(handler, time);
        await Assert.ThrowsAnyAsync<Exception>(() => ai.AnalyzeErrorAsync("boom"));
        time.Advance(TimeSpan.FromSeconds(30)); // well under the 2-minute cooldown

        await Assert.ThrowsAsync<InvalidOperationException>(() => ai.AnalyzeErrorAsync("too soon"));

        Assert.Equal(1, handler.CallCount); // the second call never reached the network at all
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<HttpStatusCode> _responses;
        public int CallCount { get; private set; }

        public ScriptedHandler(params HttpStatusCode[] responses) => _responses = new Queue<HttpStatusCode>(responses);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            var status = _responses.Count > 0 ? _responses.Dequeue() : HttpStatusCode.OK;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{}") });
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
