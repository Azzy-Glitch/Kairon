using System.Text.Json;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// No raw secret-bearing text crosses into persistent audit storage or the application logs. Every
/// test goes through the real component (AuditService, PlatformAuditService, the Serilog pipeline),
/// not through Redaction.Scrub directly.
/// </summary>
public sealed class AuditAndLogRedactionTests : IDisposable
{
    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // Every value must be absent from whatever is persisted or written.
    private static readonly string[] Secrets =
    {
        "AccessTok3nValue", "ClientS3cretValue", "Sess10nValue", "BearerTok3nValue", "C00kieValue",
        "SetC00kieValue", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.c2lnbmF0dXJlLXZhbHVl",
        "sk-abcdefghijklmnop1234", "AIzaSyD1234567890abcdefghijk", "AKIAABCDEFGHIJKLMNOP",
        "ghp_abcdefghijklmnopqrstuvwxyz0123456789", "krn_livekey12345678", "ksi_install12345678",
        "pair_code12345678", "P@ssw0rdValue", "alice@example.com", "10.20.30.40",
        "NamedTok3n", "NamedCl1entSecret", "NamedSess10n", "NamedC00kie", "NamedAuthValue", "NamedConnPwd"
    };

    // The same secrets as free text, the way an exception message or a header dump carries them.
    private static readonly string[] FreeText =
    {
        "GET /orders?access_token=AccessTok3nValue&page=2",
        "client_secret=ClientS3cretValue",
        "X-Session-Id=Sess10nValue",
        "Authorization: Bearer BearerTok3nValue",
        "Cookie: sid=C00kieValue; theme=dark",
        "Set-Cookie: auth=SetC00kieValue; HttpOnly",
        "jwt eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.c2lnbmF0dXJlLXZhbHVl seen",
        "openai sk-abcdefghijklmnop1234 rejected",
        "google AIzaSyD1234567890abcdefghijk rejected",
        "aws AKIAABCDEFGHIJKLMNOP rejected",
        "github ghp_abcdefghijklmnopqrstuvwxyz0123456789 rejected",
        "kairon krn_livekey12345678 ksi_install12345678 pair_code12345678",
        "Server=db;User ID=sa;Password=P@ssw0rdValue;",
        "contact alice@example.com from 10.20.30.40"
    };

    private static object SecretBearingData() => new
    {
        Rule = "repeated-errors",
        MaxPerIncident = 3,
        Notes = FreeText,
        // Secrets under sensitive field names, which no key=value text pattern can see in JSON.
        AccessToken = "NamedTok3n",
        Client = new { ClientSecret = "NamedCl1entSecret", Authorization = "NamedAuthValue" },
        Headers = new Dictionary<string, string>
        {
            ["X-Session-Id"] = "NamedSess10n",
            ["Cookie"] = "NamedC00kie",
            ["Accept"] = "application/json"
        },
        ConnectionString = "NamedConnPwd",
        CredentialId = Guid.Parse("11111111-2222-3333-4444-555555555555")
    };

    private static void AssertNoSecrets(string? text)
    {
        Assert.NotNull(text);
        foreach (var secret in Secrets)
            Assert.DoesNotContain(secret, text);
    }

    [Fact]
    public async Task IncidentAuditDataJsonIsRedactedBeforeItIsPersisted()
    {
        var incident = _h.SeedIncident();
        _h.Audit.Record(incident, "TestEvent", "tester", data: SecretBearingData());
        await _h.Db.SaveChangesAsync();

        await using var fresh = _h.CreateAdditionalDbContext();
        var persisted = (await fresh.IncidentEvents.AsNoTracking().SingleAsync(e => e.EventType == "TestEvent")).DataJson;

        AssertNoSecrets(persisted);
        // Still valid, still structured, and the non-sensitive facts survive.
        using var doc = JsonDocument.Parse(persisted!);
        var root = doc.RootElement;
        Assert.Equal("repeated-errors", root.GetProperty("rule").GetString());
        Assert.Equal(3, root.GetProperty("maxPerIncident").GetInt32());
        Assert.Equal(FreeText.Length, root.GetProperty("notes").GetArrayLength());
        Assert.Equal("application/json", root.GetProperty("headers").GetProperty("Accept").GetString());
        Assert.Equal("[redacted]", root.GetProperty("accessToken").GetString());
        // An identifier is not a secret: a credential id stays useful for the audit trail.
        Assert.Equal("11111111-2222-3333-4444-555555555555", root.GetProperty("credentialId").GetString());
    }

    [Fact]
    public async Task IncidentAuditRedactsBeforeTruncatingSoNoSecretFragmentSurvives()
    {
        var incident = _h.SeedIncident();
        // A JWT straddles the 8000-character bound: {"text":" (9) + 7942 + " " puts its third
        // segment across the cut. Truncated first, the remaining two segments would no longer look
        // like a JWT to any pattern and would be stored in the clear.
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTYifQ.c2lnbmF0dXJlLXZhbHVl";
        _h.Audit.Record(incident, "Big", "tester", data: new { Text = new string('x', 7942) + " " + jwt });
        await _h.Db.SaveChangesAsync();

        var persisted = (await _h.Db.IncidentEvents.AsNoTracking().SingleAsync(e => e.EventType == "Big")).DataJson!;
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", persisted);
        Assert.DoesNotContain("eyJzdWIiOiIxMjM0NTYifQ", persisted);
    }

    [Fact]
    public async Task PlatformAuditDataJsonIsRedactedIncludingNamedFields()
    {
        _h.EnsureProject();
        var audit = new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance);
        audit.Record("test.action", "tester", "project", _h.ProjectId.ToString(), _h.ProjectId, data: SecretBearingData());
        await _h.Db.SaveChangesAsync();

        var persisted = (await _h.Db.PlatformAuditEvents.AsNoTracking().SingleAsync(e => e.Action == "test.action")).DataJson;
        AssertNoSecrets(persisted);
        using var doc = JsonDocument.Parse(persisted!);
        Assert.Equal("repeated-errors", doc.RootElement.GetProperty("rule").GetString());
    }

    private sealed class CaptureSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Microsoft.Extensions.Logging.ILogger logger, CaptureSink sink, Serilog.Core.Logger serilog) RedactedLogger()
    {
        var sink = new CaptureSink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(new RedactingLogSink(sink))
            .CreateLogger();
        // The application logs through Microsoft.Extensions.Logging into Serilog, as here.
        var logger = new SerilogLoggerFactory(serilog).CreateLogger("Kairon.Tests");
        return (logger, sink, serilog);
    }

    private static string Rendered(LogEvent e)
    {
        using var writer = new StringWriter();
        e.RenderMessage(writer);
        return writer + " " + string.Join(" ", e.Properties.Select(p => $"{p.Key}={p.Value}")) + " " + e.Exception;
    }

    [Fact]
    public void StructuredLogPropertiesExceptionsAndTemplatesAreRedactedCentrally()
    {
        var (logger, sink, serilog) = RedactedLogger();
        using (serilog)
        {
            var exception = new InvalidOperationException(string.Join(" | ", FreeText));
            logger.LogError(exception,
                "AI call for {Incident} failed: {Detail} token={ApiKey} payload {@Payload} headers {Headers}",
                "INC-0001", string.Join(" | ", FreeText), "NamedTok3n",
                new { AccessToken = "NamedTok3n", Attempts = 2 },
                new Dictionary<string, string> { ["Cookie"] = "NamedC00kie", ["X-Session-Id"] = "NamedSess10n" });
            // An interpolated message puts the data in the template text itself.
            logger.LogWarning($"leaked via interpolation: client_secret=NamedCl1entSecret Authorization: Bearer BearerTok3nValue");
        }

        Assert.Equal(2, sink.Events.Count);
        foreach (var e in sink.Events)
            AssertNoSecrets(Rendered(e));

        var error = sink.Events[0];
        // Structure is preserved: same level, same property names, non-sensitive values intact.
        Assert.Equal(LogEventLevel.Error, error.Level);
        Assert.Equal(new[] { "ApiKey", "Detail", "Headers", "Incident", "Payload" },
            error.Properties.Keys.Where(k => k != "SourceContext").OrderBy(k => k));
        Assert.Equal("\"INC-0001\"", error.Properties["Incident"].ToString());
        var payload = Assert.IsType<StructureValue>(error.Properties["Payload"]);
        Assert.Equal("2", payload.Properties.Single(p => p.Name == "Attempts").Value.ToString());
        Assert.Contains("InvalidOperationException", error.Exception!.ToString());
        Assert.Contains("[redacted]", error.Exception.Message);
    }

    [Fact]
    public void ScrubbingIsIdempotentSoCallSiteAndCentralRedactionDoNotCompound()
    {
        var (logger, sink, serilog) = RedactedLogger();
        using (serilog)
            logger.LogWarning("AI unavailable: {Reason}", Redaction.Scrub("api_key=sk-abcdefghijklmnop1234 from 10.20.30.40"));

        Assert.Equal("\"api_key=[redacted] from [redacted-ip]\"", sink.Events.Single().Properties["Reason"].ToString());
    }

    [Fact]
    public void ProductionLogSinksWriteOnlyRedactedText()
    {
        var logs = Path.Combine(Path.GetTempPath(), "kairon-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var logger = new LoggerConfiguration().WriteToRedactedSinks(logs).CreateLogger())
            {
                logger.Error(new InvalidOperationException("boom access_token=AccessTok3nValue"),
                    "Request {Path} from {Client}", "/x?client_secret=ClientS3cretValue", "alice@example.com");
            }

            var text = string.Join("\n", Directory.GetFiles(logs).Select(File.ReadAllText));
            Assert.Contains("[redacted]", text);
            Assert.Contains("InvalidOperationException", text);
            AssertNoSecrets(text);
        }
        finally
        {
            if (Directory.Exists(logs)) Directory.Delete(logs, recursive: true);
        }
    }
}
