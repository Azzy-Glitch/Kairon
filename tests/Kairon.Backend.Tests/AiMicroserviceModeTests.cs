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

    private AiMicroservice CreateMicroservice(bool staticMockMode)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["AiService:MockMode"] = staticMockMode.ToString() })
            .Build();

        return new AiMicroservice(
            new HttpClient { BaseAddress = new Uri("http://127.0.0.1:1") },
            configuration,
            Options.Create(new AiOrchestrationOptions()),
            _configService,
            NullLogger<AiMicroservice>.Instance);
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
    public async Task NoSavedConfigurationPreservesTheExistingStaticMockModeFalse()
    {
        var ai = CreateMicroservice(staticMockMode: false);

        Assert.Equal("live", await ai.GetModeAsync());
    }

    [Fact]
    public async Task ASavedConfigurationOverridesStaticMockModeTrue()
    {
        await _configService.SaveAsync("groq", "gsk_real_looking_key", "openai/gpt-oss-120b");
        var ai = CreateMicroservice(staticMockMode: true);

        Assert.Equal("groq", await ai.GetModeAsync());
    }

    [Fact]
    public async Task ASavedConfigurationMeansAnalyzeErrorNoLongerReturnsTheHardcodedMock()
    {
        await _configService.SaveAsync("groq", "gsk_real_looking_key", "openai/gpt-oss-120b");
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
}
