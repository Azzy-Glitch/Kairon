using System.Text.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>Kairon.Backend Windows-service mode: machine-wide data root, per-start operator key
/// handoff, and the supervised AI child's environment.</summary>
public sealed class BackendServiceModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kairon-service-mode-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void DataRootGivesTheFullManagedLayoutUnderTheServiceFolder()
    {
        var paths = KaironDataPaths.Resolve(new PersistenceOptions { DataRoot = _root });

        Assert.True(paths.ManagedLayout);
        Assert.Equal(Path.Combine(_root, "data", "kairon.db"), paths.DatabasePath);
        Assert.Equal(Path.Combine(_root, "logs"), paths.Logs);
        Assert.Equal(Path.Combine(_root, "config"), paths.Config);
        Assert.Equal(Path.Combine(_root, "backups"), paths.Backups);
    }

    [Fact]
    public void AnExplicitDatabasePathStillWinsOverTheDataRoot()
    {
        var database = Path.Combine(_root, "explicit", "kairon.db");
        var paths = KaironDataPaths.Resolve(new PersistenceOptions { DataRoot = Path.Combine(_root, "service"), DatabasePath = database });

        Assert.Equal(database, paths.DatabasePath);
        Assert.False(paths.ManagedLayout);
    }

    [Fact]
    public void WithoutADataRootTheDesktopLayoutIsUnchanged()
    {
        var paths = KaironDataPaths.Resolve(new PersistenceOptions());
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kairon"), paths.Root);
    }

    [Fact]
    public void OperatorKeyIsFreshOnEveryStartAndPublishedWhole()
    {
        var file = Path.Combine(_root, "operator", "operator.key");

        var first = OperatorKeyFile.Provision(file);
        var second = OperatorKeyFile.Provision(file);

        Assert.Matches("^[0-9A-F]{64}$", first);
        Assert.NotEqual(first, second);
        Assert.Equal(second, File.ReadAllText(file));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(file)!)); // no temp files left behind
    }

    [Fact]
    public void SupervisedAiServiceGetsOnlyItsTransportSecretAndAPrivateTempFolder()
    {
        var supervisor = new AiServiceSupervisor(@"C:\Program Files\Kairon\ai\Kairon.AI.exe", "TRANSPORT", Path.Combine(_root, "ai-tmp"),
            NullLogger<AiServiceSupervisor>.Instance);

        var info = supervisor.BuildStartInfo();

        Assert.Equal("TRANSPORT", info.Environment["KAIRON_AI_API_KEY"]);
        Assert.Equal(Path.Combine(_root, "ai-tmp"), info.Environment["TEMP"]);
        Assert.Equal(Path.Combine(_root, "ai-tmp"), info.Environment["TMP"]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(@"C:\Program Files\Kairon\ai", info.WorkingDirectory);
    }

    [Fact]
    public async Task CredentialListShowsWhichSdkAPairedCredentialBelongsTo()
    {
        using var h = new TestHarness();
        h.EnsureProject();
        h.Db.ProjectApiCredentials.AddRange(
            new ProjectApiCredential { ProjectId = h.ProjectId, Name = "python-sdk", KeyPrefix = "krn_a", KeyHash = "a" },
            new ProjectApiCredential { ProjectId = h.ProjectId, Name = "dotnet-sdk", KeyPrefix = "krn_b", KeyHash = "b" },
            new ProjectApiCredential { ProjectId = h.ProjectId, Name = "ci-pipeline", KeyPrefix = "krn_c", KeyHash = "c" });
        h.Db.SaveChanges();
        var controller = new ProjectsController(h.Db,
            new ProjectCredentialService(h.Db, TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System,
                new PlatformAuditService(h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance)),
            new PlatformAuditService(h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);

        var result = Assert.IsType<OkObjectResult>(await controller.ListCredentials(h.ProjectId, default));
        var json = JsonSerializer.Serialize(result.Value);
        var items = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(json)!;

        Assert.Equal("python", items.Single(i => i["Name"].GetString() == "python-sdk")["SdkType"].GetString());
        Assert.Equal("dotnet", items.Single(i => i["Name"].GetString() == "dotnet-sdk")["SdkType"].GetString());
        Assert.Equal(JsonValueKind.Null, items.Single(i => i["Name"].GetString() == "ci-pipeline")["SdkType"].ValueKind);
        Assert.DoesNotContain("KeyHash", json);
    }
}
