using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// The frontend AI Configuration panel's persistence: a real key, round-tripped through Data
/// Protection encryption, never returned in plain text by anything except the one internal
/// decrypt method the controller never exposes.
/// </summary>
public sealed class AiProviderConfigServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly IAiProviderConfigService _service;

    public AiProviderConfigServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        // Ephemeral, in-memory key ring - no real file-system writes in a unit test, and no
        // dependency on this machine's actual DPAPI state.
        var dataProtection = new EphemeralDataProtectionProvider();
        _service = new AiProviderConfigService(_db, dataProtection, TimeProvider.System);
    }

    [Fact]
    public async Task NothingSavedYetReportsNotConfigured()
    {
        var summary = await _service.GetAsync();

        Assert.False(summary.HasApiKey);
        Assert.Equal(string.Empty, summary.Provider);
        Assert.Null(summary.UpdatedAt);
        Assert.False(await _service.HasValidConfigurationAsync());
        Assert.Null(await _service.GetDecryptedApiKeyAsync());
    }

    [Fact]
    public async Task SaveNeverReturnsTheRawKey()
    {
        var summary = await _service.SaveAsync("groq", "gsk_super_secret_value", "openai/gpt-oss-120b");

        Assert.True(summary.HasApiKey);
        Assert.Equal("groq", summary.Provider);
        Assert.Equal("openai/gpt-oss-120b", summary.Model);
        Assert.DoesNotContain("gsk_super_secret_value", summary.ToString());
    }

    [Fact]
    public async Task TheStoredKeyDecryptsBackToExactlyWhatWasSaved()
    {
        await _service.SaveAsync("groq", "gsk_super_secret_value", null);

        Assert.Equal("gsk_super_secret_value", await _service.GetDecryptedApiKeyAsync());
        Assert.True(await _service.HasValidConfigurationAsync());
    }

    [Fact]
    public async Task EncryptedValueNeverContainsThePlainKey()
    {
        await _service.SaveAsync("groq", "gsk_super_secret_value", null);

        // Reach past the service to the raw stored row - the whole point of encrypting at rest is
        // that the ciphertext itself never contains the secret in a recognizable form.
        var stored = await _db.AiProviderConfigs.SingleAsync();
        Assert.DoesNotContain("gsk_super_secret_value", stored.EncryptedApiKey);
    }

    [Fact]
    public async Task OmittingTheKeyOnASecondSaveKeepsThePreviousOne()
    {
        await _service.SaveAsync("groq", "gsk_original_key", "model-a");

        var summary = await _service.SaveAsync("groq", null, "model-b");

        Assert.True(summary.HasApiKey);
        Assert.Equal("model-b", summary.Model);
        Assert.Equal("gsk_original_key", await _service.GetDecryptedApiKeyAsync());
    }

    [Fact]
    public async Task BlankModelMeansAutoRecommended()
    {
        var summary = await _service.SaveAsync("groq", "gsk_x", "  ");

        Assert.Equal(string.Empty, summary.Model);
    }

    [Fact]
    public async Task SecondSaveUpdatesTheSameSingletonRowRatherThanCreatingAnother()
    {
        await _service.SaveAsync("groq", "gsk_x", "model-a");
        await _service.SaveAsync("qwen", "qwen_x", "model-b");

        Assert.Equal(1, await _db.AiProviderConfigs.CountAsync());
        var summary = await _service.GetAsync();
        Assert.Equal("qwen", summary.Provider);
    }

    [Fact]
    public async Task BlankProviderIsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SaveAsync("  ", "gsk_x", null));
    }

    [Fact]
    public async Task GetSelectionReflectsTheStoredProviderAndModel()
    {
        await _service.SaveAsync("gemini", "AIza_x", "gemini-2.5-flash-lite");

        var selection = await _service.GetSelectionAsync();

        Assert.NotNull(selection);
        Assert.Equal("gemini", selection!.Value.Provider);
        Assert.Equal("gemini-2.5-flash-lite", selection.Value.Model);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
