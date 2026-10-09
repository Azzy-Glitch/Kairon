using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.DTOs.Sre;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Evidence;
using Kairon.Backend.Services.Orchestration;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Kairon.Backend.Services.Verification;
using Kairon.SDK;
using Kairon.SDK.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Application-process remediation: a plain SDK-connected app (no Windows service, no wrapper, no
/// permission script) can be restarted by KAIRON after approval. Which process is decided by the
/// operating system (the Agent's view of the proof connection), not by the app; the restart is
/// performed by the UserAgent in the owner's session; recovery is verified from fresh telemetry.
/// </summary>
public sealed class AppProcessRemediationTests : IDisposable
{
    private static readonly string AppFolder = Path.Combine(Path.GetTempPath(), "kairon-apps", "checkout");
    private static readonly string AppExecutable = Path.Combine(AppFolder, ".venv", "Scripts", "python.exe");
    private const int SessionId = 1;

    private readonly TestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // --- 1. The full journey, on the real services --------------------------------------------

    [Theory]
    [InlineData("python")]
    [InlineData("dotnet")]
    public async Task ConnectedAppIsRestartedAfterApprovalAndTheIncidentResolvesOnVerifiedRecovery(string sdkType)
    {
        var h = _h;
        h.EnsureProject();
        const string agentKey = "app-process-agent-proof-key-123456789012345";
        const string userAgentKey = "app-process-user-agent-key-1234567890123456";
        var agents = new AgentRegistrationService(h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        await agents.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "dev-laptop", OperatingSystem = "Windows", Architecture = "x64",
            AgentVersion = "1.1", AgentKey = agentKey, UserAgentKey = userAgentKey
        }, default);
        var platformAudit = new PlatformAuditService(h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance);
        var credentials = new ProjectCredentialService(h.Db, Options.Create(new PlatformSecurityOptions()), TimeProvider.System, platformAudit);
        var pairing = new SdkPairingService(h.Db, credentials, TimeProvider.System, Options.Create(new ProductOptions()),
            platformAudit, NullLogger<SdkPairingService>.Instance);
        var created = (await pairing.CreateAsync(h.ProjectId, sdkType, default))!;
        var paired = (await pairing.RedeemAsync(created.Code, sdkType, "1.1.0", default))!;
        Assert.True(await pairing.ConfirmAsync(created.PairingId, paired.ApiKey, default));
        var bindings = new MachineTelemetryBindingService(h.Db, agents, h.Targets, Options.Create(new WindowsRemediationOptions()));
        var telemetry = new PlatformTelemetryService(h.Db, h.Queue, TimeProvider.System);

        // The app runs: its process (4242, as Windows reports it to the Agent) sends telemetry, and
        // the UserAgent in the user's session reports that same process.
        var started = DateTime.UtcNow.AddMinutes(-3);
        for (var i = 0; i < 3; i++)
            await SendMetricAsync(h, bindings, telemetry, credentials, paired.ApiKey, agentKey, machineId,
                DateTime.UtcNow.AddSeconds(-20 + i * 5), cpu: 96, sdkType, processId: 4242);
        await agents.RecordUserSessionHeartbeatAsync(machineId, userAgentKey, UserSession(4242, started), default);

        var binding = h.Db.SdkMachineBindings.AsNoTracking().Single();
        Assert.Equal(4242, binding.ProcessId);
        Assert.Equal(AppFolder, binding.ProcessWorkingDirectory);

        // One click: everything derived server-side from the pairing session.
        var management = new RemediationTargetManagementService(h.Db, platformAudit, TimeProvider.System,
            Options.Create(h.WindowsRemediation), h.WindowsServices, h.WindowsServices, h.Targets);
        var enabled = await management.EnableAppRestartForPairingAsync(created.PairingId, "operator-test");
        Assert.Equal(RemediationTargetOperationOutcome.Success, enabled.Outcome);
        Assert.Equal(RemediationTargetKinds.AppProcess, enabled.Target!.Kind);
        Assert.Equal(4242, enabled.Target.ProcessId);
        // A new or changed target always needs a fresh Agent confirmation first (the app's next
        // telemetry, seconds later) - it never inherits confirmation from before it existed.
        Assert.Equal("AwaitingAgentConfirmation", enabled.Target.Readiness);

        // Telemetry after the target exists is machine-scoped; detection and correlation follow.
        for (var i = 0; i < 3; i++)
            await SendMetricAsync(h, bindings, telemetry, credentials, paired.ApiKey, agentKey, machineId,
                DateTime.UtcNow.AddSeconds(-14 + i * 5), cpu: 96, sdkType, processId: 4242);
        var targetRow = h.Db.RemediationTargets.AsNoTracking().Single();
        Assert.Equal(TargetReadiness.Ready, (await h.Targets.EvaluateTargetAsync(targetRow, ServiceToolNames.RestartApplication)).Readiness);
        var signals = await h.CreateDetectionEngine().EvaluateAsync(h.ProjectId, h.Environment, h.Service);
        Assert.NotEmpty(signals);
        var correlated = await h.CreateCorrelationEngine().CorrelateAsync(signals);
        var incident = correlated.Single(i => IncidentMachineScope.GetMachineId(i) == machineId);

        var queue = new ProcessRestartQueue(h.Db, TimeProvider.System);
        var userAgentRestarts = 0;
        var restart = new RestartApplicationTool(h.Db, h.Targets, afterQueued: async (commandId, ct) =>
        {
            // The UserAgent in session 1 claims the approved restart, restarts the app (new process
            // 5151) and reports it; the new process then reports in like any app would.
            var claimed = Assert.Single(await queue.ClaimAsync(machineId, SessionId, ct));
            Assert.Equal((4242, AppExecutable, AppFolder), (claimed.ProcessId, claimed.Executable, claimed.WorkingDirectory));
            Assert.True(await queue.CompleteAsync(machineId, commandId, succeeded: true, newProcessId: 5151, error: null, ct));
            userAgentRestarts++;
        });
        var registry = new RemediationToolRegistry([restart]);
        Assert.NotNull(await restart.TargetFingerprintAsync(incident));
        var policy = new RemediationPolicy(registry, Options.Create(h.Remediation), NullLogger<RemediationPolicy>.Instance);
        var executor = new RemediationExecutor(registry, policy, h.Audit, h.Db, Options.Create(h.Remediation),
            NullLogger<RemediationExecutor>.Instance);
        var verifier = new VerificationService(h.Db, h.Audit, new RemediationToolRegistryAccessor(registry),
            Options.Create(h.Verification), Options.Create(h.Detection), NullLogger<VerificationService>.Instance);
        var recovery = new AsyncBeforeVerification(verifier, async () =>
        {
            await agents.RecordUserSessionHeartbeatAsync(machineId, userAgentKey, UserSession(5151, DateTime.UtcNow.AddSeconds(-2)), default);
            await SendMetricAsync(h, bindings, telemetry, credentials, paired.ApiKey, agentKey, machineId,
                DateTime.UtcNow, cpu: 12, sdkType, processId: 5151);
        });
        var orchestrator = new IncidentOrchestrator(h.Db, h.CreateDetectionEngine(), h.CreateCorrelationEngine(),
            new EvidenceCollector(h.Db, registry, Options.Create(h.AiOptions), Options.Create(h.Detection),
                NullLogger<EvidenceCollector>.Instance), h.Ai, policy, registry, executor, recovery, h.Audit,
            new IncidentKeyGenerator(h.Db), h.Queue, Options.Create(h.AiOptions), Options.Create(h.Remediation),
            NullLogger<IncidentOrchestrator>.Instance);

        var ai = FakeAiService.DefaultResult();
        ai.Recommendations = [new AiRecommendationDto
        {
            Action = ServiceToolNames.RestartApplication, Reason = "Runaway work inside the app process",
            ExpectedOutcome = "CPU returns below threshold", RiskLevel = "medium"
        }];
        h.Ai.NextResult = ai;
        await orchestrator.InvestigateAsync(incident.Id);

        // The AI was offered exactly this restart for this app, and nothing ran before approval.
        Assert.Contains(h.Ai.LastEvidence!.AvailableActions, a => a.Action == ServiceToolNames.RestartApplication);
        Assert.Equal(IncidentStatus.AwaitingApproval, incident.Status);
        var action = Assert.Single(h.Db.RemediationActions.Where(a => a.IncidentId == incident.Id));
        Assert.Equal(0, userAgentRestarts);
        Assert.Empty(h.Db.ProcessRestartCommands);

        await orchestrator.ApproveAsync(incident.Id, action.Id, "operator-test", null);
        await orchestrator.ExecuteAndVerifyAsync(incident.Id, action.Id);

        Assert.Equal(1, userAgentRestarts);
        Assert.Equal(RemediationStatus.Executed, action.Status);
        Assert.Equal(VerificationStatus.Passed, incident.VerificationState);
        Assert.Equal(IncidentStatus.Resolved, incident.Status);
        Assert.Equal(ProcessRestartStatus.Succeeded, h.Db.ProcessRestartCommands.AsNoTracking().Single().Status);
    }

    // --- 2. Which process: only the one Windows names -------------------------------------------

    [Fact]
    public void TheSdkReportedFolderIsKeptOnlyForTheProcessTheAgentObserved()
    {
        var binding = new SdkMachineBinding();
        MachineTelemetryBindingService.RecordProcess(binding, 4242, Body(4242, AppFolder));
        Assert.Equal((4242, AppFolder, AppExecutable), (binding.ProcessId, binding.ProcessWorkingDirectory, binding.ProcessExecutable));

        // Another process claims a folder: not taken, and the new process inherits nothing.
        MachineTelemetryBindingService.RecordProcess(binding, 9000, Body(4242, Path.Combine(Path.GetTempPath(), "elsewhere")));
        Assert.Equal(9000, binding.ProcessId);
        Assert.Null(binding.ProcessWorkingDirectory);

        // An older Agent that cannot name the process: nothing is known.
        MachineTelemetryBindingService.RecordProcess(binding, null, Body(4242, AppFolder));
        Assert.Null(binding.ProcessId);
        Assert.Null(binding.ProcessWorkingDirectory);
    }

    [Fact]
    public void ARelativeOrMalformedFolderIsNeverRecorded()
    {
        var binding = new SdkMachineBinding();
        MachineTelemetryBindingService.RecordProcess(binding, 4242, Body(4242, "relative\\folder"));
        Assert.Null(binding.ProcessWorkingDirectory);
        MachineTelemetryBindingService.RecordProcess(binding, 4242, Encoding.UTF8.GetBytes("{not json"));
        Assert.Null(binding.ProcessWorkingDirectory);
    }

    [Fact]
    public async Task TheAgentConfirmationCarriesTheProcessItObserved()
    {
        _h.EnsureProject();
        const string agentKey = "confirm-agent-proof-key-12345678901234567890";
        var agents = new AgentRegistrationService(_h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        await agents.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "dev-laptop", OperatingSystem = "Windows", Architecture = "x64",
            AgentVersion = "1.1", AgentKey = agentKey, UserAgentKey = "confirm-user-agent-key-1234567890123456789"
        }, default);
        var credential = _h.SeedCredential(_h.ProjectId, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("krn_confirm_proof_key_123456"))));
        credential.KeyPrefix = "krn_confirm_";
        _h.Db.SaveChanges();
        var bindings = new MachineTelemetryBindingService(_h.Db, agents, _h.Targets, Options.Create(new WindowsRemediationOptions()));
        var proof = await bindings.CreateProofAsync(_h.ProjectId, _h.Environment, _h.Service, new string('A', 64), "krn_confirm_proof_key_123456", default);
        Assert.NotNull(proof);

        Assert.True(await bindings.ConfirmProofAsync(proof!.Value, machineId, agentKey, default, processId: 4242));
        Assert.Equal(4242, _h.Db.SdkMachineProofChallenges.AsNoTracking().Single().ProcessId);
    }

    // --- 3. Readiness: every reason it cannot run, reported precisely ---------------------------

    [Fact]
    public async Task AReadyAppTargetResolvesToTheObservedProcessAndUserSession()
    {
        var (_, target) = SeedReadyAppTarget();
        var evaluation = await _h.Targets.EvaluateTargetAsync(target, ServiceToolNames.RestartApplication);

        Assert.Equal(TargetReadiness.Ready, evaluation.Readiness);
        Assert.Equal((4242, SessionId, AppExecutable, AppFolder),
            (evaluation.Target!.ProcessId, evaluation.Target.SessionId, evaluation.Target.ProcessExecutable, evaluation.Target.ProcessWorkingDirectory));
    }

    [Fact]
    public async Task AnUnidentifiedProcessIsReportedAsSuch()
    {
        var (binding, target) = SeedReadyAppTarget();
        binding.ProcessId = null;
        _h.Db.SaveChanges();
        Assert.Equal(TargetReadiness.ProcessUnknown, (await _h.Targets.EvaluateTargetAsync(target, null)).Readiness);
    }

    [Fact]
    public async Task WithoutTheUsersUserAgentNothingCanRestartTheApp()
    {
        var (_, target) = SeedReadyAppTarget();
        foreach (var app in _h.Db.DiscoveredApplications) app.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
        _h.Db.SaveChanges();
        Assert.Equal(TargetReadiness.UserAgentOffline, (await _h.Targets.EvaluateTargetAsync(target, null)).Readiness);
    }

    [Fact]
    public async Task KaironsOwnProcessesAreNeverRestarted()
    {
        var (_, target) = SeedReadyAppTarget(executable: Path.Combine(AppFolder, "Kairon.UserAgent.exe"));
        Assert.Equal(TargetReadiness.Denylisted, (await _h.Targets.EvaluateTargetAsync(target, null)).Readiness);
    }

    [Fact]
    public async Task ATargetKindOnlyEverCarriesItsOwnOperations()
    {
        var (_, appTarget) = SeedReadyAppTarget();
        Assert.Equal(TargetReadiness.OperationNotAllowed,
            (await _h.Targets.EvaluateTargetAsync(appTarget, ServiceToolNames.RestartService)).Readiness);

        appTarget.Kind = RemediationTargetKinds.WindowsService;
        appTarget.WindowsServiceName = "ScopedService";
        appTarget.AllowedOperationsJson = RemediationTargetOperations.Serialize([ServiceToolNames.RestartApplication]);
        Assert.Equal(TargetReadiness.OperationNotAllowed,
            (await _h.Targets.EvaluateTargetAsync(appTarget, ServiceToolNames.RestartApplication)).Readiness);
    }

    // --- 4. Product-managed configuration --------------------------------------------------------

    [Fact]
    public async Task AppTargetsAreValidatedForTheirKindAndNeedNoWindowsPermission()
    {
        var (machine, credential) = SeedMachineAndCredential();
        var management = Management();
        CreateRemediationTargetRequest Request(string service, string winsvc, params string[] ops) => new()
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = service, MachineId = machine.Id,
            TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName, WindowsServiceName = winsvc,
            Kind = RemediationTargetKinds.AppProcess, AllowedOperations = [.. ops], Enabled = true
        };

        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed,
            (await management.CreateAsync(Request("A", "ScopedService", ServiceToolNames.RestartApplication), "op")).Outcome);
        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed,
            (await management.CreateAsync(Request("B", "", ServiceToolNames.RestartService), "op")).Outcome);

        var created = await management.CreateAsync(Request(_h.Service, "", ServiceToolNames.RestartApplication), "op");
        Assert.Equal(RemediationTargetOperationOutcome.Success, created.Outcome);
        var stored = _h.Db.RemediationTargets.AsNoTracking().Single();
        Assert.Equal(RemediationTargetKinds.AppProcess, stored.Kind);
        Assert.Null(stored.ServiceIdentityHash);

        var preflight = (await management.PreflightAsync(stored.Id))!;
        Assert.Null(preflight.FixCommand);
        Assert.Empty(preflight.RequiredRights);
        Assert.DoesNotContain(preflight.Checks, c => c.Key is "platform" or "service" || c.Key.StartsWith("right-"));
        Assert.Contains(preflight.Checks, c => c.Key == "process");
        Assert.Contains(preflight.Checks, c => c.Key == "useragent");
    }

    [Fact]
    public async Task OneClickFromAPairingIsIdempotentAndNeverOverridesAnotherTarget()
    {
        var (sessionId, credentialId, machine) = SeedConnectedPairing();
        var management = Management();

        var first = await management.EnableAppRestartForPairingAsync(sessionId, "op");
        var second = await management.EnableAppRestartForPairingAsync(sessionId, "op");
        Assert.Equal(RemediationTargetOperationOutcome.Success, first.Outcome);
        Assert.Equal(first.Target!.Id, second.Target!.Id);
        Assert.Single(_h.Db.RemediationTargets);
        Assert.Equal((machine.Id, credentialId, _h.Service, "Development"),
            (first.Target.MachineId, first.Target.TelemetryCredentialId, first.Target.Service, first.Target.Environment));

        // A Windows-service target already covering the service is never replaced behind its back.
        var target = _h.Db.RemediationTargets.Single();
        target.Kind = RemediationTargetKinds.WindowsService;
        target.WindowsServiceName = "ScopedService";
        _h.Db.SaveChanges();
        Assert.Equal(RemediationTargetOperationOutcome.Conflict, (await management.EnableAppRestartForPairingAsync(sessionId, "op")).Outcome);
    }

    [Fact]
    public async Task OneClickExplainsWhatIsMissingBeforeTheAppIsConfirmed()
    {
        var (sessionId, _, _) = SeedConnectedPairing(confirmed: false);
        var result = await Management().EnableAppRestartForPairingAsync(sessionId, "op");
        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("app-not-connected", result.ErrorCode);
        Assert.Empty(_h.Db.RemediationTargets);
    }

    // --- 5. The restart tool and the UserAgent queue ---------------------------------------------

    [Fact]
    public async Task NothingIsQueuedWhenTheApprovedProgramIsNoLongerTheOneRunning()
    {
        var (_, target) = SeedReadyAppTarget();
        var incident = ScopedIncident(target.MachineId);
        var tool = new RestartApplicationTool(_h.Db, _h.Targets);
        var approved = (await tool.TargetFingerprintAsync(incident))!;

        // A different program now holds the app's connection.
        foreach (var app in _h.Db.DiscoveredApplications) app.Executable = Path.Combine(AppFolder, "other.exe");
        _h.Db.SaveChanges();

        var result = await tool.ExecuteAsync(Context(incident, approved));
        Assert.False(result.Success);
        Assert.Empty(_h.Db.ProcessRestartCommands);
    }

    [Fact]
    public async Task AnUnclaimedRestartExpiresAndReportsThatNothingChanged()
    {
        var (_, target) = SeedReadyAppTarget();
        var incident = ScopedIncident(target.MachineId);
        using var fast = FastTimings();
        var tool = new RestartApplicationTool(_h.Db, _h.Targets);

        var result = await tool.ExecuteAsync(Context(incident, (await tool.TargetFingerprintAsync(incident))!));

        Assert.False(result.Success);
        Assert.StartsWith("UserAgentOffline:", result.Error);
        Assert.Equal(ProcessRestartStatus.Expired, _h.Db.ProcessRestartCommands.AsNoTracking().Single().Status);
    }

    [Fact]
    public async Task AFailedRestartIsReportedWithTheUserAgentsReason()
    {
        var (_, target) = SeedReadyAppTarget();
        var incident = ScopedIncident(target.MachineId);
        var queue = new ProcessRestartQueue(_h.Db, TimeProvider.System);
        var tool = new RestartApplicationTool(_h.Db, _h.Targets, afterQueued: async (id, ct) =>
        {
            Assert.Single(await queue.ClaimAsync(target.MachineId, SessionId, ct));
            await queue.CompleteAsync(target.MachineId, id, false, null, "The application did not exit within 10 seconds.", ct);
        });

        var result = await tool.ExecuteAsync(Context(incident, (await tool.TargetFingerprintAsync(incident))!));

        Assert.False(result.Success);
        Assert.Equal("ProcessRestartFailed: The application did not exit within 10 seconds.", result.Error);
    }

    [Fact]
    public async Task TheQueueHandsAnInstructionOnlyToItsOwnMachineAndSessionAndOnlyOnce()
    {
        var machineId = Guid.NewGuid();
        var queue = new ProcessRestartQueue(_h.Db, TimeProvider.System);
        var command = new ProcessRestartCommand
        {
            IncidentId = Guid.NewGuid(), ActionKey = "ACT-1", ProjectId = _h.ProjectId, MachineId = machineId, SessionId = SessionId,
            ProcessId = 4242, ProcessStartedAt = DateTime.UtcNow, Executable = AppExecutable, WorkingDirectory = AppFolder,
            ExpiresAt = DateTime.UtcNow.AddMinutes(1)
        };
        var expired = new ProcessRestartCommand
        {
            IncidentId = Guid.NewGuid(), ActionKey = "ACT-2", ProjectId = _h.ProjectId, MachineId = machineId, SessionId = SessionId,
            ProcessId = 1, ProcessStartedAt = DateTime.UtcNow, Executable = AppExecutable, WorkingDirectory = AppFolder,
            ExpiresAt = DateTime.UtcNow.AddSeconds(-1)
        };
        _h.Db.ProcessRestartCommands.AddRange(command, expired);
        _h.Db.SaveChanges();

        Assert.Empty(await queue.ClaimAsync(Guid.NewGuid(), SessionId, default));   // another machine
        Assert.Empty(await queue.ClaimAsync(machineId, SessionId + 1, default));     // another user session
        Assert.Equal(command.Id, Assert.Single(await queue.ClaimAsync(machineId, SessionId, default)).Id);
        Assert.Empty(await queue.ClaimAsync(machineId, SessionId, default));         // never twice
        Assert.Equal(ProcessRestartStatus.Expired, _h.Db.ProcessRestartCommands.AsNoTracking().Single(c => c.Id == expired.Id).Status);

        Assert.False(await queue.CompleteAsync(Guid.NewGuid(), command.Id, true, 5151, null, default)); // wrong machine
        Assert.True(await queue.CompleteAsync(machineId, command.Id, true, 5151, null, default));
        Assert.False(await queue.CompleteAsync(machineId, command.Id, false, null, "late", default));   // already done
    }

    [Fact]
    public async Task ARealConcurrentUserAgentCanClaimAndCompleteWhileTheToolWaits()
    {
        // A file database with a connection per side: the tool and the UserAgent run concurrently,
        // exactly as in production.
        var path = Path.Combine(Path.GetTempPath(), $"kairon-app-restart-{Guid.NewGuid():N}.db");
        var connection = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, DefaultTimeout = 30 }.ToString();
        AppDbContext NewDb() => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        try
        {
            Guid machineId;
            SreIncident incident;
            await using (var setup = NewDb())
            {
                await setup.Database.EnsureCreatedAsync();
                (machineId, incident) = SeedReadyAppTargetInto(setup);
            }
            using var fast = FastTimings();
            await using var toolDb = NewDb();
            var resolver = new RemediationTargetResolver(toolDb, Options.Create(new WindowsRemediationOptions()), FakeWindowsServices.Default);
            var tool = new RestartApplicationTool(toolDb, resolver);
            var fingerprint = (await tool.TargetFingerprintAsync(incident))!;

            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var userAgent = Task.Run(async () =>
            {
                await using var db = NewDb();
                var queue = new ProcessRestartQueue(db, TimeProvider.System);
                while (true)
                {
                    var claimed = await queue.ClaimAsync(machineId, SessionId, stop.Token);
                    if (claimed.Count == 1)
                    {
                        await queue.CompleteAsync(machineId, claimed[0].Id, true, 5151, null, stop.Token);
                        return;
                    }
                    await Task.Delay(50, stop.Token);
                }
            });

            var result = await tool.ExecuteAsync(Context(incident, fingerprint));
            await userAgent;

            Assert.True(result.Success, result.Error);
            Assert.Equal("5151", result.Details["newProcessId"]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OnlyTheMachinesUserAgentKeyCanClaimOrReportRestarts()
    {
        _h.EnsureProject();
        var agents = new AgentRegistrationService(_h.Db, TimeProvider.System);
        var machineId = Guid.NewGuid();
        const string agentKey = "endpoint-auth-agent-key-12345678901234567890";
        const string userAgentKey = "endpoint-auth-user-agent-key-1234567890123456";
        await agents.RegisterAsync(new AgentRegistrationDto
        {
            MachineId = machineId, HostName = "dev-laptop", OperatingSystem = "Windows", Architecture = "x64",
            AgentVersion = "1.1", AgentKey = agentKey, UserAgentKey = userAgentKey
        }, default);
        var queue = new ProcessRestartQueue(_h.Db, TimeProvider.System);
        AgentController Controller(string key)
        {
            var controller = new AgentController(agents, _h.Db, TimeProvider.System)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            controller.Request.Headers["X-Kairon-Agent-Key"] = key;
            return controller;
        }

        // Neither a wrong key nor the Agent's own machine key may act as the UserAgent.
        foreach (var key in new[] { "wrong-key-wrong-key-wrong-key", agentKey })
        {
            Assert.IsType<UnauthorizedObjectResult>(await Controller(key).ClaimProcessRestarts(machineId,
                new ClaimProcessRestartsDto { SessionId = SessionId }, queue, default));
            Assert.IsType<UnauthorizedObjectResult>(await Controller(key).CompleteProcessRestart(machineId, Guid.NewGuid(),
                new ProcessRestartResultDto { Succeeded = true }, queue, default));
        }
        Assert.IsType<OkObjectResult>(await Controller(userAgentKey).ClaimProcessRestarts(machineId,
            new ClaimProcessRestartsDto { SessionId = SessionId }, queue, default));
    }

    [Fact]
    public async Task RecoveryRequiresADifferentProcessToReportAfterTheRestart()
    {
        var (binding, target) = SeedReadyAppTarget();
        var incident = ScopedIncident(target.MachineId);
        var tool = new RestartApplicationTool(_h.Db, _h.Targets);
        var fingerprint = (await tool.TargetFingerprintAsync(incident))!;
        _h.Db.ProcessRestartCommands.Add(new ProcessRestartCommand
        {
            IncidentId = incident.Id, ActionKey = "ACT-1", ProjectId = _h.ProjectId, MachineId = target.MachineId, SessionId = SessionId,
            ProcessId = 4242, ProcessStartedAt = DateTime.UtcNow, Executable = AppExecutable, WorkingDirectory = AppFolder,
            Status = ProcessRestartStatus.Succeeded, CompletedAt = DateTime.UtcNow.AddSeconds(-5), NewProcessId = 5151,
            ExpiresAt = DateTime.UtcNow
        });
        _h.Db.SaveChanges();

        Assert.False(await tool.IsDesiredStateAsync(incident, fingerprint, default)); // still the old process

        binding.ProcessId = 5151;
        binding.LastConfirmedAt = DateTime.UtcNow;
        foreach (var app in _h.Db.DiscoveredApplications) app.ProcessId = 5151;
        _h.Db.SaveChanges();
        Assert.True(await tool.IsDesiredStateAsync(incident, fingerprint, default));
    }

    // --- helpers -----------------------------------------------------------------------------------

    private RemediationTargetManagementService Management() =>
        new(_h.Db, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance),
            TimeProvider.System, Options.Create(_h.WindowsRemediation), _h.WindowsServices, _h.WindowsServices, _h.Targets);

    private (Machine Machine, ProjectApiCredential Credential) SeedMachineAndCredential()
    {
        _h.EnsureProject();
        var machine = _h.SeedMachine(agentCredentialHash: "agent-hash");
        var credential = _h.SeedCredential(_h.ProjectId, "credential-hash");
        return (machine, credential);
    }

    private (SdkMachineBinding Binding, RemediationTarget Target) SeedReadyAppTarget(string? executable = null)
    {
        var (machine, credential) = SeedMachineAndCredential();
        var (binding, target) = AppTargetRows(_h.ProjectId, _h.Environment, _h.Service, machine, credential.Id, executable ?? AppExecutable);
        _h.Db.AddRange(binding, target, UserAgentProcess(machine.Id, 4242, executable ?? AppExecutable));
        _h.Db.SaveChanges();
        return (binding, target);
    }

    private (Guid MachineId, SreIncident Incident) SeedReadyAppTargetInto(AppDbContext db)
    {
        db.Projects.Add(new Models.Platform.Project { Id = _h.ProjectId, Name = "Orders", Slug = "orders", IsActive = true, CreatedAt = DateTime.UtcNow });
        var machine = new Machine
        {
            Id = Guid.NewGuid(), HostName = "enrolled-host", OperatingSystem = "Windows", AgentCredentialHash = "agent-hash",
            RegisteredAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow
        };
        var credential = new ProjectApiCredential { ProjectId = _h.ProjectId, KeyHash = "credential-hash" };
        db.AddRange(machine, credential);
        var (binding, target) = AppTargetRows(_h.ProjectId, _h.Environment, _h.Service, machine, credential.Id, AppExecutable);
        var incident = new SreIncident
        {
            IncidentKey = "INC-0001", ProjectId = _h.ProjectId, Timestamp = DateTime.UtcNow, Application = "Checkout", Service = _h.Service,
            Environment = _h.Environment, Severity = IncidentSeverity.High, Status = IncidentStatus.Detected, Title = "Checkout degradation",
            CorrelationKey = "k", CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { Rule = "cpu", MachineId = machine.Id } })
        };
        db.AddRange(binding, target, UserAgentProcess(machine.Id, 4242, AppExecutable), incident);
        db.SaveChanges();
        return (machine.Id, incident);
    }

    private static (SdkMachineBinding, RemediationTarget) AppTargetRows(Guid projectId, string environment, string service,
        Machine machine, Guid credentialId, string executable) =>
        (new SdkMachineBinding
        {
            CredentialId = credentialId, ProjectId = projectId, MachineId = machine.Id, AgentCredentialHash = machine.AgentCredentialHash,
            LastConfirmedAt = DateTime.UtcNow, ProcessId = 4242, ProcessWorkingDirectory = AppFolder, ProcessExecutable = executable
        },
        new RemediationTarget
        {
            ProjectId = projectId, Environment = environment, EnvironmentNormalized = environment.ToLowerInvariant(), Service = service,
            MachineId = machine.Id, TelemetryCredentialId = credentialId, ExpectedHostName = machine.HostName, WindowsServiceName = "",
            Kind = RemediationTargetKinds.AppProcess, AllowedOperationsJson = RemediationTargetOperations.Serialize([ServiceToolNames.RestartApplication]),
            Enabled = true, UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
        });

    private static DiscoveredApplication UserAgentProcess(Guid machineId, int processId, string executable) => new()
    {
        Id = Guid.NewGuid(), MachineId = machineId, ProcessId = processId, ProcessStartedAt = DateTime.UtcNow.AddMinutes(-5),
        Name = "python", Executable = executable, Source = "UserAgent", SessionId = SessionId, UserName = "DEV\\developer",
        IsRunning = true, FirstSeenAt = DateTime.UtcNow, LastSeenAt = DateTime.UtcNow
    };

    private (Guid SessionId, Guid CredentialId, Machine Machine) SeedConnectedPairing(bool confirmed = true)
    {
        var (machine, credential) = SeedMachineAndCredential();
        var session = new SdkPairingSession
        {
            ProjectId = _h.ProjectId, SdkType = "python", CodeHash = Guid.NewGuid().ToString("N"), CreatedAt = DateTime.UtcNow.AddMinutes(-2),
            ExpiresAt = DateTime.UtcNow.AddMinutes(8), RedeemedAt = DateTime.UtcNow.AddMinutes(-1), IssuedCredentialId = credential.Id,
            ConfirmedAt = confirmed ? DateTime.UtcNow.AddMinutes(-1) : null, Environment = _h.Environment, Service = _h.Service
        };
        _h.Db.SdkPairingSessions.Add(session);
        var (binding, _) = AppTargetRows(_h.ProjectId, _h.Environment, _h.Service, machine, credential.Id, AppExecutable);
        _h.Db.AddRange(binding, UserAgentProcess(machine.Id, 4242, AppExecutable));
        _h.Db.SaveChanges();
        return (session.Id, credential.Id, machine);
    }

    private SreIncident ScopedIncident(Guid machineId)
    {
        var incident = _h.SeedIncident();
        incident.CorrelatedMetricsJson = SreJson.Serialize(new[] { new CorrelatedSignalSnapshot { Rule = "cpu", MachineId = machineId } });
        _h.Db.SaveChanges();
        return incident;
    }

    private static RemediationToolContext Context(SreIncident incident, string fingerprint) => new()
    {
        ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey, Environment = incident.Environment,
        Service = incident.Service, ActionKey = "ACT-test", Parameters = new Dictionary<string, string> { ["targetFingerprint"] = fingerprint }
    };

    private static byte[] Body(int processId, string cwd) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        events = new[] { new { processId, metadata = new Dictionary<string, string> { ["process.cwd"] = cwd, ["process.executable"] = AppExecutable } } }
    });

    private static UserSessionHeartbeatDto UserSession(int processId, DateTime startedAt) => new()
    {
        Timestamp = DateTime.UtcNow, SessionId = SessionId, UserName = "DEV\\developer",
        Processes = [new UserProcessSnapshotDto { ProcessId = processId, StartedAt = startedAt, Name = "python", Executable = AppExecutable }]
    };

    private static async Task SendMetricAsync(TestHarness h, MachineTelemetryBindingService bindings, PlatformTelemetryService telemetry,
        ProjectCredentialService credentials, string sdkKey, string agentKey, Guid machineId, DateTime timestamp, double cpu,
        string sdkType, int processId)
    {
        var web = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var metadata = new Dictionary<string, string> { ["process.cwd"] = AppFolder, ["process.executable"] = AppExecutable, ["cpu.scope"] = "process" };
        byte[] body;
        if (sdkType == "dotnet")
        {
            // The real .NET SDK formatter, then the folder of the simulated app process.
            var item = NormalizedTelemetryEvent.From(new MetricPayload
            {
                ProjectId = h.ProjectId, Timestamp = timestamp, Application = "Checkout", Service = h.Service,
                Environment = h.Environment, CpuPercent = cpu, RequestCount = 10
            }, new KaironOptions { ProjectId = h.ProjectId });
            Assert.Equal(Environment.ProcessId, item.ProcessId);
            Assert.Equal("process", item.Metadata!["cpu.scope"]);
            Assert.Equal(Environment.CurrentDirectory, item.Metadata["process.cwd"]);
            body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Events = new[] { new { item.EventId, item.ProjectId, item.Timestamp, item.EventType, item.Severity, item.Source,
                    item.Application, item.Service, item.Environment, item.Runtime, item.SourceVersion, item.ResourceMetrics,
                    ProcessId = processId, Metadata = metadata } }
            }, web);
        }
        else
        {
            body = JsonSerializer.SerializeToUtf8Bytes(new NormalizedTelemetryBatchDto
            {
                Events = [new NormalizedTelemetryEventDto
                {
                    EventId = Guid.NewGuid(), ProjectId = h.ProjectId, Timestamp = timestamp, EventType = "metric", Severity = "Information",
                    Source = "python-sdk", Application = "Checkout", Service = h.Service, Environment = h.Environment, Runtime = "Python 3.14",
                    ProcessId = processId, Metadata = metadata,
                    ResourceMetrics = new ResourceTelemetryMetricsDto { CpuPercent = cpu, RequestCount = 10 }
                }]
            }, web);
        }
        var batch = JsonSerializer.Deserialize<NormalizedTelemetryBatchDto>(body, web)!;
        var proof = await bindings.CreateProofAsync(h.ProjectId, h.Environment, h.Service, Convert.ToHexString(SHA256.HashData(body)), sdkKey, default);
        Assert.NotNull(proof);
        // The Agent names the process from the OS TCP table; the app never chooses it.
        Assert.True(await bindings.ConfirmProofAsync(proof!.Value, machineId, agentKey, default, processId));
        var controller = new PlatformTelemetryController(telemetry, credentials, null!, bindings, Options.Create(new PlatformSecurityOptions()))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.Request.Body = new MemoryStream(body);
        controller.Request.Headers["X-Kairon-API-Key"] = sdkKey;
        controller.Request.Headers["X-Kairon-Machine-Proof"] = proof.Value.ToString();
        Assert.IsType<OkObjectResult>((await controller.Ingest(batch, default)).Result);
    }

    private static IDisposable FastTimings()
    {
        var (claim, completion, poll) = (RestartApplicationTool.ClaimWindow, RestartApplicationTool.CompletionWindow, RestartApplicationTool.PollInterval);
        RestartApplicationTool.ClaimWindow = TimeSpan.FromSeconds(2);
        RestartApplicationTool.CompletionWindow = TimeSpan.FromSeconds(2);
        RestartApplicationTool.PollInterval = TimeSpan.FromMilliseconds(50);
        return new Restore(() => (RestartApplicationTool.ClaimWindow, RestartApplicationTool.CompletionWindow, RestartApplicationTool.PollInterval) = (claim, completion, poll));
    }

    private sealed class Restore(Action action) : IDisposable { public void Dispose() => action(); }

    private sealed class AsyncBeforeVerification(IVerificationService inner, Func<Task> before) : IVerificationService
    {
        public async Task<VerificationResult> VerifyAsync(SreIncident incident, RemediationAction action, CancellationToken cancellationToken = default)
        {
            await before();
            return await inner.VerifyAsync(incident, action, cancellationToken);
        }
    }
}
