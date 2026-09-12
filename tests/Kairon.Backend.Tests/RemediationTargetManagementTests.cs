using System.Net;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Audit;
using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Phase 2: the operator-authorized remediation-target management API
/// (RemediationTargetsController / RemediationTargetManagementService), built on the Phase 1
/// persistence + resolver foundation covered by WindowsServiceRemediationTests,
/// MachineTelemetryAuthorizationTests and DetectionTests. These tests deliberately verify actual
/// persistence and runtime-resolver effects, not merely controller status codes.
/// </summary>
public sealed class RemediationTargetManagementTests : IDisposable
{
    private readonly TestHarness _h = new();

    private RemediationTargetManagementService Service() =>
        new(_h.Db, new PlatformAuditService(_h.Db, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);

    private CreateRemediationTargetRequest ValidRequest(Machine machine, ProjectApiCredential credential) => new()
    {
        ProjectId = _h.ProjectId,
        Environment = _h.Environment,
        Service = _h.Service,
        MachineId = machine.Id,
        TelemetryCredentialId = credential.Id,
        ExpectedHostName = machine.HostName,
        WindowsServiceName = "ScopedService",
        AllowedOperations = [ServiceToolNames.RestartService, ServiceToolNames.RunHealthCheck]
    };

    // --- Create: happy path ---

    [Fact]
    public async Task ValidTargetSucceedsAndIsAuditedAndAllowedOperationsAreDeterministicallyOrdered()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var request = ValidRequest(machine, credential);
        request.AllowedOperations = [ServiceToolNames.RunHealthCheck, ServiceToolNames.RestartService]; // reversed input order

        var result = await Service().CreateAsync(request, "alice@kairon", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        Assert.Equal(_h.ProjectId, result.Target!.ProjectId);
        Assert.Equal(machine.Id, result.Target.MachineId);
        Assert.True(result.Target.Enabled);
        // Serialize orders by the fixed Known order (RestartService before RunHealthCheck),
        // regardless of the order the caller supplied them in - RemediationTargetOperations.Serialize.
        Assert.Equal([ServiceToolNames.RestartService, ServiceToolNames.RunHealthCheck], result.Target.AllowedOperations);
        Assert.Equal(machine.HostName, result.Target.MachineHostName);
        Assert.Equal(credential.Name, result.Target.TelemetryCredentialName);

        var persisted = Assert.Single(_h.Db.RemediationTargets);
        Assert.Equal(request.WindowsServiceName, persisted.WindowsServiceName);

        var audit = Assert.Single(_h.Db.PlatformAuditEvents);
        Assert.Equal("remediation-target.created", audit.Action);
        Assert.Equal("alice@kairon", audit.Actor);
        Assert.Contains("ScopedService", audit.DataJson);
        // No secret ever appears in the audit trail for this entity.
        Assert.DoesNotContain(machine.AgentCredentialHash, audit.DataJson);
    }

    [Fact]
    public async Task DisabledTargetCreationSkipsRelationshipValidation()
    {
        // A disabled target may reference a machine/credential that doesn't exist yet - RemediationTarget.Enabled's
        // own documented invariant (a disabled row may harmlessly go stale / be pre-staged). The
        // project itself must still exist (a real FK constraint applies regardless of Enabled).
        _h.EnsureProject();
        var request = new CreateRemediationTargetRequest
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service,
            MachineId = Guid.NewGuid(), TelemetryCredentialId = Guid.NewGuid(),
            ExpectedHostName = "future-host", WindowsServiceName = "ScopedService",
            AllowedOperations = [ServiceToolNames.RestartService], Enabled = false
        };

        var result = await Service().CreateAsync(request, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        Assert.False(result.Target!.Enabled);
    }

    // --- Create: rejection matrix ---

    [Theory]
    [InlineData("invalid-project")]
    [InlineData("invalid-machine")]
    [InlineData("cross-project-credential")]
    [InlineData("revoked-credential")]
    [InlineData("invalid-environment")]
    [InlineData("invalid-hostname")]
    [InlineData("hostname-mismatch")]
    [InlineData("invalid-service-name")]
    [InlineData("unknown-operation")]
    [InlineData("empty-operations")]
    [InlineData("os-not-windows")]
    [InlineData("unenrolled-machine")]
    public async Task CreateRejectsEveryInvalidTargetShape(string scenario)
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var request = ValidRequest(machine, credential);

        switch (scenario)
        {
            case "invalid-project": request.ProjectId = Guid.NewGuid(); break;
            case "invalid-machine": request.MachineId = Guid.NewGuid(); break;
            case "cross-project-credential":
                // Machine has no ProjectId column in this schema - project isolation for a
                // remediation target is enforced entirely through TelemetryCredentialId's own
                // ProjectId ownership, matching IRemediationTargetResolver's own design. Binding a
                // target to another project's credential is this system's actual "cross-project"
                // violation.
                var otherProject = new Project { Name = "other-project" };
                _h.Db.Projects.Add(otherProject);
                _h.Db.SaveChanges();
                var otherCredential = _h.SeedCredential(otherProject.Id);
                request.TelemetryCredentialId = otherCredential.Id;
                break;
            case "revoked-credential":
                credential.RevokedAt = DateTime.UtcNow;
                _h.Db.SaveChanges();
                break;
            case "invalid-environment": request.Environment = "Staging2"; break;
            case "invalid-hostname": request.ExpectedHostName = "bad host!"; break;
            case "hostname-mismatch": request.ExpectedHostName = "totally-different-host"; break;
            case "invalid-service-name": request.WindowsServiceName = "bad service!"; break;
            case "unknown-operation": request.AllowedOperations = ["DeleteEverything"]; break;
            case "empty-operations": request.AllowedOperations = []; break;
            case "os-not-windows": machine.OperatingSystem = "Linux"; _h.Db.SaveChanges(); break;
            case "unenrolled-machine": machine.AgentCredentialHash = ""; _h.Db.SaveChanges(); break;
        }

        var result = await Service().CreateAsync(request, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, result.Outcome);
        Assert.Equal("invalid-target", result.ErrorCode);
        Assert.Empty(_h.Db.RemediationTargets);
    }

    [Fact]
    public async Task DuplicateEnabledTargetIsRejectedNotSilentlySelected()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var existing = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        var machine2 = _h.SeedMachine(hostName: "second-host");
        var request = ValidRequest(machine2, credential); // same ProjectId+Environment+Service as `existing`

        var result = await Service().CreateAsync(request, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Conflict, result.Outcome);
        Assert.Equal("duplicate-target", result.ErrorCode);
        // Neither silently overwritten nor a second row silently created.
        var only = Assert.Single(_h.Db.RemediationTargets);
        Assert.Equal(existing.Id, only.Id);
        Assert.Equal(machine.Id, only.MachineId);
    }

    [Fact]
    public async Task SqliteConstraintClassificationDistinguishesUniqueFromForeignKeyAndNotNullFailures()
    {
        // Empirically validates the assumption IsUniqueConstraintViolation depends on: SQLite's
        // primary SqliteErrorCode (19, SQLITE_CONSTRAINT) is IDENTICAL for every kind of constraint
        // failure - unique, foreign-key, and NOT NULL alike - so matching on it alone (the
        // previous, overly-broad behavior) would misclassify a foreign-key or NOT NULL failure as
        // a duplicate-target conflict. SqliteExtendedErrorCode is what actually discriminates.
        // Goes through EF's own SaveChangesAsync (not hand-written SQL) so Guid columns are
        // encoded exactly as the real code path encodes them - this is also the exact
        // DbUpdateException-wrapping-SqliteException shape IsUniqueConstraintViolation itself
        // pattern-matches against.
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName); // occupies the unique key

        async Task<Microsoft.Data.Sqlite.SqliteException> SaveAndCaptureAsync(RemediationTarget target)
        {
            using var db = _h.CreateAdditionalDbContext();
            db.RemediationTargets.Add(target);
            var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            return Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(ex.InnerException);
        }

        var uniqueEx = await SaveAndCaptureAsync(new RemediationTarget
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service, // same key as the seeded target
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "AnotherSvc", AllowedOperationsJson = "[]", Enabled = true
        });
        var foreignKeyEx = await SaveAndCaptureAsync(new RemediationTarget
        {
            ProjectId = Guid.NewGuid(), // genuinely does not exist
            Environment = "Staging", Service = "OtherSvc",
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "AnotherSvc", AllowedOperationsJson = "[]", Enabled = true
        });
        var notNullEx = await SaveAndCaptureAsync(new RemediationTarget
        {
            ProjectId = _h.ProjectId, Environment = "Staging", Service = "OtherSvc2",
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = null!, AllowedOperationsJson = "[]", Enabled = true
        });

        // The old, overly-broad check would have treated all three identically.
        Assert.Equal(19, uniqueEx.SqliteErrorCode);
        Assert.Equal(19, foreignKeyEx.SqliteErrorCode);
        Assert.Equal(19, notNullEx.SqliteErrorCode);

        // The extended code is what genuinely discriminates - only the unique violation is 2067.
        Assert.Equal(2067, uniqueEx.SqliteExtendedErrorCode);
        Assert.NotEqual(2067, foreignKeyEx.SqliteExtendedErrorCode);
        Assert.NotEqual(2067, notNullEx.SqliteExtendedErrorCode);
    }

    [Fact]
    public async Task DisabledDuplicatesMayCoexistWithAnEnabledTarget()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName); // enabled

        var machine2 = _h.SeedMachine(hostName: "second-host");
        var request = ValidRequest(machine2, credential);
        request.Enabled = false;

        var result = await Service().CreateAsync(request, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        Assert.Equal(2, _h.Db.RemediationTargets.Count());
    }

    // --- Read ---

    [Fact]
    public async Task ListAndGetReflectDatabaseStateIncludingDisabledTargets()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var enabled = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, service: "SvcA");
        var disabled = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, service: "SvcB", enabled: false);

        var service = Service();
        var all = await service.ListAsync(new RemediationTargetFilter(null, null, null, null), default);
        Assert.Equal(2, all.Count);

        var onlyEnabled = await service.ListAsync(new RemediationTargetFilter(null, null, true, null), default);
        Assert.Equal(enabled.Id, Assert.Single(onlyEnabled).Id);

        var onlyDisabled = await service.ListAsync(new RemediationTargetFilter(null, null, false, null), default);
        Assert.Equal(disabled.Id, Assert.Single(onlyDisabled).Id);

        Assert.NotNull(await service.GetAsync(disabled.Id, default));
        Assert.Null(await service.GetAsync(Guid.NewGuid(), default));
    }

    [Fact]
    public async Task ControllerGetMissingReturns404WithoutLeakingInternals()
    {
        var controller = new RemediationTargetsController(Service());
        var result = await controller.Get(Guid.NewGuid(), default);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var body = Assert.IsType<ApiResponse<object>>(notFound.Value);
        Assert.False(body.Success);
        Assert.Equal("target-not-found", body.ErrorCode);
    }

    [Fact]
    public async Task ControllerCreateReturns201WithLocation()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var controller = new RemediationTargetsController(Service())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.Create(ValidRequest(machine, credential), default);

        var created = Assert.IsType<CreatedResult>(result);
        Assert.Contains("/api/v1/remediation-targets/", created.Location);
    }

    // --- Update ---

    [Fact]
    public async Task ValidUpdateSucceedsAndAudited()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        var service = Service();

        var update = new UpdateRemediationTargetRequest
        {
            ProjectId = target.ProjectId, Environment = target.Environment, Service = target.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "RenamedService", AllowedOperations = [ServiceToolNames.StopService], Enabled = true
        };

        var result = await service.UpdateAsync(target.Id, update, "bob@kairon", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        Assert.Equal("RenamedService", result.Target!.WindowsServiceName);
        Assert.Equal([ServiceToolNames.StopService], result.Target.AllowedOperations);

        var audit = Assert.Single(_h.Db.PlatformAuditEvents.Where(e => e.Action == "remediation-target.updated"));
        Assert.Contains("\"before\"", audit.DataJson); // SreJson serializes camelCase
        Assert.Contains("RenamedService", audit.DataJson);
    }

    [Fact]
    public async Task UpdateNotFoundReturnsNotFound()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var update = new UpdateRemediationTargetRequest
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "Svc", AllowedOperations = [ServiceToolNames.RestartService], Enabled = true
        };

        var result = await Service().UpdateAsync(Guid.NewGuid(), update, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task UpdateEnforcesUniquenessAgainstAnotherEnabledTarget()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, service: "SvcA");
        var toUpdate = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName, service: "SvcB");

        var update = new UpdateRemediationTargetRequest
        {
            ProjectId = toUpdate.ProjectId, Environment = toUpdate.Environment, Service = "SvcA", // collides
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = toUpdate.WindowsServiceName, AllowedOperations = [ServiceToolNames.RestartService], Enabled = true
        };

        var result = await Service().UpdateAsync(toUpdate.Id, update, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Conflict, result.Outcome);
        Assert.Equal("SvcB", _h.Db.RemediationTargets.Single(t => t.Id == toUpdate.Id).Service); // unchanged
    }

    [Theory]
    [InlineData("invalid-machine")]
    [InlineData("invalid-credential")]
    [InlineData("unknown-operation")]
    public async Task UpdateRejectsInvalidFields(string scenario)
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        var update = new UpdateRemediationTargetRequest
        {
            ProjectId = target.ProjectId, Environment = target.Environment, Service = target.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = target.WindowsServiceName, AllowedOperations = [ServiceToolNames.RestartService], Enabled = true
        };
        switch (scenario)
        {
            case "invalid-machine": update.MachineId = Guid.NewGuid(); break;
            case "invalid-credential": update.TelemetryCredentialId = Guid.NewGuid(); break;
            case "unknown-operation": update.AllowedOperations = ["NotARealOperation"]; break;
        }

        var result = await Service().UpdateAsync(target.Id, update, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, result.Outcome);
        // The original row is untouched by a rejected update.
        Assert.Equal(machine.Id, _h.Db.RemediationTargets.Single(t => t.Id == target.Id).MachineId);
    }

    [Fact]
    public async Task EnablingAnInvalidDisabledTargetIsRejected()
    {
        // Staged disabled with a credential that doesn't exist - allowed, since disabled targets
        // skip relationship validation at create time (see DisabledTargetCreationSkipsRelationshipValidation).
        // The project itself must still exist (a real FK constraint applies regardless of Enabled).
        _h.EnsureProject();
        var machine = _h.SeedMachine();
        var request = new CreateRemediationTargetRequest
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service,
            MachineId = machine.Id, TelemetryCredentialId = Guid.NewGuid(), ExpectedHostName = machine.HostName,
            WindowsServiceName = "ScopedService", AllowedOperations = [ServiceToolNames.RestartService], Enabled = false
        };
        var service = Service();
        var created = (await service.CreateAsync(request, "op", default)).Target!;

        var result = await service.SetEnabledAsync(created.Id, true, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.ValidationFailed, result.Outcome);
        Assert.False(_h.Db.RemediationTargets.Single(t => t.Id == created.Id).Enabled);
    }

    [Fact]
    public async Task StaleConcurrentUpdateIsRejected()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        var service = Service();
        var readByOperatorA = (await service.GetAsync(target.Id, default))!;

        // Operator B updates first.
        await service.UpdateAsync(target.Id, new UpdateRemediationTargetRequest
        {
            ProjectId = target.ProjectId, Environment = target.Environment, Service = target.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "ChangedByB", AllowedOperations = [ServiceToolNames.RestartService], Enabled = true
        }, "operator-b", default);

        // Operator A submits based on the now-stale UpdatedAt it originally read.
        var staleResult = await service.UpdateAsync(target.Id, new UpdateRemediationTargetRequest
        {
            ProjectId = target.ProjectId, Environment = target.Environment, Service = target.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "OverwrittenByA", AllowedOperations = [ServiceToolNames.RestartService], Enabled = true,
            ExpectedUpdatedAt = readByOperatorA.UpdatedAt
        }, "operator-a", default);

        Assert.Equal(RemediationTargetOperationOutcome.Conflict, staleResult.Outcome);
        Assert.Equal("stale-update", staleResult.ErrorCode);
        Assert.Equal("ChangedByB", _h.Db.RemediationTargets.Single(t => t.Id == target.Id).WindowsServiceName);
    }

    // --- Disable / Delete ---

    [Fact]
    public async Task DisablingWorksAndTargetRemainsForHistory()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        var result = await Service().SetEnabledAsync(target.Id, false, "op", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, result.Outcome);
        var persisted = Assert.Single(_h.Db.RemediationTargets); // retained, not physically deleted
        Assert.False(persisted.Enabled);
    }

    [Fact]
    public async Task ControllerDeleteDisablesAndReturns204()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        var controller = new RemediationTargetsController(Service())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        var result = await controller.Delete(target.Id, default);

        Assert.IsType<NoContentResult>(result);
        Assert.False(_h.Db.RemediationTargets.Single(t => t.Id == target.Id).Enabled);
    }

    [Fact]
    public async Task ControllerDeleteMapsAConcurrentStaleDeleteTo409NotAGeneric422()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        await dbA.RemediationTargets.SingleAsync(t => t.Id == target.Id);
        await dbB.RemediationTargets.SingleAsync(t => t.Id == target.Id);
        var controllerA = new RemediationTargetsController(new RemediationTargetManagementService(dbA,
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        var controllerB = new RemediationTargetsController(new RemediationTargetManagementService(dbB,
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System))
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

        Assert.IsType<NoContentResult>(await controllerA.Delete(target.Id, default));
        var second = await controllerB.Delete(target.Id, default);

        var conflict = Assert.IsType<ConflictObjectResult>(second);
        var body = Assert.IsType<ApiResponse<object>>(conflict.Value);
        Assert.Equal("stale-update", body.ErrorCode);
    }

    [Fact]
    public async Task DisabledTargetNoLongerResolvesAtRuntimeForAnyConsumer()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);
        await Service().SetEnabledAsync(target.Id, false, "op", default);

        var resolver = _h.Targets;
        var detection = await resolver.ResolveDetectionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, default);
        Assert.Equal(DetectionTargetOutcome.NoTarget, detection.Outcome);

        Assert.Null(await resolver.ResolveTelemetryTargetAsync(_h.ProjectId, machine.Id, _h.Environment, _h.Service, default));
        Assert.Null(await resolver.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService, default));
    }

    // --- Security: the management API never executes, and never weakens runtime re-validation ---

    [Fact]
    public async Task ManagementWritesNeverTouchIncidentsOrRemediationActions()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var service = Service();
        var created = (await service.CreateAsync(ValidRequest(machine, credential), "op", default)).Target!;
        await service.UpdateAsync(created.Id, new UpdateRemediationTargetRequest
        {
            ProjectId = created.ProjectId, Environment = created.Environment, Service = created.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "Renamed", AllowedOperations = created.AllowedOperations, Enabled = true
        }, "op", default);
        await service.SetEnabledAsync(created.Id, false, "op", default);
        await service.SetEnabledAsync(created.Id, true, "op", default);

        Assert.Empty(_h.Db.SreIncidents);
        Assert.Empty(_h.Db.RemediationActions);
    }

    [Fact]
    public async Task UpdatingTheTargetNaturallyInvalidatesAPreviouslyApprovedFingerprintWithoutTheServiceKnowingAboutFingerprints()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var service = Service();
        var created = (await service.CreateAsync(ValidRequest(machine, credential), "op", default)).Target!;

        var incident = _h.SeedIncident();
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        _h.Db.SaveChanges();

        var tool = new RestartServiceTool(_h.Db, _h.Targets, new Scm());
        var originalFingerprint = await tool.TargetFingerprintAsync(incident);
        Assert.NotNull(originalFingerprint);

        await service.UpdateAsync(created.Id, new UpdateRemediationTargetRequest
        {
            ProjectId = created.ProjectId, Environment = created.Environment, Service = created.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "ADifferentService", AllowedOperations = created.AllowedOperations, Enabled = true
        }, "op", default);

        Assert.NotEqual(originalFingerprint, await tool.TargetFingerprintAsync(incident));
    }

    [Fact]
    public async Task RevokingTheCredentialAfterCreationStillBlocksExecutionWithoutAnyManagementApiCall()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var created = (await Service().CreateAsync(ValidRequest(machine, credential), "op", default)).Target!;

        credential.RevokedAt = DateTime.UtcNow;
        _h.Db.SaveChanges();

        Assert.Null(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService, default));
    }

    [Fact]
    public async Task StaleHeartbeatStillBlocksExecutionRegardlessOfManagementApiValidationAtCreateTime()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        await Service().CreateAsync(ValidRequest(machine, credential), "op", default);

        machine.LastSeenAt = DateTime.UtcNow.AddMinutes(-10);
        _h.Db.SaveChanges();

        Assert.Null(await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService, default));
    }

    // --- Runtime integration: consumers see exactly what the management API wrote ---

    [Fact]
    public async Task ExecutionResolverSeesATargetCreatedThroughTheManagementApi()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        await Service().CreateAsync(ValidRequest(machine, credential), "op", default);

        var resolved = await _h.Targets.ResolveExecutionTargetAsync(_h.ProjectId, _h.Environment, _h.Service, ServiceToolNames.RestartService, default);

        Assert.NotNull(resolved);
        Assert.Equal(machine.Id, resolved!.MachineId);
        Assert.Equal(credential.Id, resolved.TelemetryCredentialId);
    }

    [Fact]
    public async Task WindowsServiceToolExecutesAgainstATargetCreatedThroughTheManagementApi()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        await Service().CreateAsync(ValidRequest(machine, credential), "op", default);

        var incident = _h.SeedIncident();
        var snapshots = SreJson.Deserialize(incident.CorrelatedMetricsJson, new List<CorrelatedSignalSnapshot>());
        snapshots.ForEach(s => s.MachineId = machine.Id);
        incident.CorrelatedMetricsJson = SreJson.Serialize(snapshots);
        _h.Db.SaveChanges();

        var scm = new Scm { State = 4 };
        var tool = new RestartServiceTool(_h.Db, _h.Targets, scm);
        var fingerprint = (await tool.TargetFingerprintAsync(incident))!;

        var result = await tool.ExecuteAsync(new RemediationToolContext
        {
            ProjectId = incident.ProjectId, IncidentId = incident.Id, IncidentKey = incident.IncidentKey,
            Environment = incident.Environment, Service = incident.Service, ActionKey = "ACT-mgmt-api",
            Parameters = new Dictionary<string, string> { ["targetFingerprint"] = fingerprint }
        });

        Assert.True(result.Success);
        // A restart from Running issues stop-then-start (WindowsServiceTool.ExecuteAsync), matching
        // WindowsServiceRemediationTests.ExactApprovedTargetCanRestartAndChangedTargetCannot's
        // identical expected call sequence for a Phase 1-seeded target.
        Assert.Equal([(machine.HostName, "ScopedService", false), (machine.HostName, "ScopedService", true)], scm.Calls);
    }

    [Fact]
    public async Task TelemetryAuthorizationReflectsATargetCreatedThroughTheManagementApi()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        // A real credential (with a real ApiKey/hash) is required to exercise header authorization,
        // unlike the fixed "test-hash" TestHarness.SeedCredential normally seeds.
        var credentials = new Kairon.Backend.Services.ProjectCredentialService(_h.Db,
            TestHarness.Opt(new PlatformSecurityOptions()), TimeProvider.System);
        var created = await credentials.CreateAsync(_h.ProjectId, "target-credential", default);
        await Service().CreateAsync(new CreateRemediationTargetRequest
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service,
            MachineId = machine.Id, TelemetryCredentialId = created!.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = "ScopedService", AllowedOperations = [ServiceToolNames.RestartService]
        }, "op", default);

        var controller = new TelemetryController(_h.Db, null!, _h.Queue, credentials,
            TestHarness.Opt(new PlatformSecurityOptions()), _h.Targets)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Request.Headers["X-Kairon-API-Key"] = created.ApiKey;

        var result = await controller.CreateMetric(new Kairon.Backend.DTOs.MetricDto
        {
            ProjectId = _h.ProjectId, MachineId = machine.Id, Environment = _h.Environment, Service = _h.Service, RequestCount = 1
        }, default);

        Assert.IsType<OkObjectResult>(result);
        Assert.Equal(machine.Id, _h.Db.Metrics.Single().MachineId);
    }

    [Fact]
    public async Task DetectionEngineScopesToTheMachineOfATargetCreatedThroughTheManagementApi()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        await Service().CreateAsync(ValidRequest(machine, credential), "op", default);

        for (var i = 0; i < 4; i++)
        {
            var metric = _h.SeedMetric(DateTime.UtcNow.AddSeconds(-30 + i * 5), cpu: 96);
            metric.MachineId = Guid.NewGuid(); // some other machine's telemetry
        }
        _h.Db.SaveChanges();

        var engine = _h.CreateDetectionEngine();
        Assert.Empty(await engine.EvaluateAsync(_h.ProjectId, _h.Environment, _h.Service));

        foreach (var metric in _h.Db.Metrics) metric.MachineId = machine.Id;
        _h.Db.SaveChanges();

        var signals = await engine.EvaluateAsync(_h.ProjectId, _h.Environment, _h.Service);
        Assert.NotEmpty(signals);
        Assert.All(signals, s => Assert.Equal(machine.Id, s.MachineId));
    }

    // --- Authorization: the whole controller requires an operator key ---

    [Fact]
    public void ControllerRequiresOperatorAuthorization()
    {
        var attribute = typeof(RemediationTargetsController).GetCustomAttributes(typeof(RequiresOperatorAttribute), inherit: false);
        Assert.NotEmpty(attribute);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("wrong-key", false)]
    [InlineData("correct-key", true)]
    public async Task OperatorAuthorizationFilterGatesEveryActionOnThisController(string? suppliedKey, bool shouldPass)
    {
        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = typeof(RemediationTargetsController).GetCustomAttributes(typeof(RequiresOperatorAttribute), inherit: false)
        };
        var httpContext = new DefaultHttpContext();
        if (suppliedKey is not null) httpContext.Request.Headers["X-Kairon-Operator-Key"] = suppliedKey;
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var context = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(), new Dictionary<string, object?>(), controller: new object());

        var filter = new OperatorAuthorizationFilter(
            TestHarness.Opt(new SreSecurityOptions { RequireOperatorKey = true, OperatorKey = "correct-key" }),
            NullLogger<OperatorAuthorizationFilter>.Instance);

        var nextCalled = false;
        await filter.OnActionExecutionAsync(context, () => { nextCalled = true; return Task.FromResult<ActionExecutedContext>(null!); });

        Assert.Equal(shouldPass, nextCalled);
        if (!shouldPass) Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    // --- Validation (preflight) endpoint ---

    [Fact]
    public async Task ValidateEndpointNeverPersistsAndReportsErrorsWithoutMutatingState()
    {
        var machine = _h.SeedMachine();
        var request = new CreateRemediationTargetRequest
        {
            ProjectId = _h.ProjectId, Environment = _h.Environment, Service = _h.Service,
            MachineId = machine.Id, TelemetryCredentialId = Guid.NewGuid(), ExpectedHostName = machine.HostName,
            WindowsServiceName = "ScopedService", AllowedOperations = [ServiceToolNames.RestartService]
        };

        var controller = new RemediationTargetsController(Service());
        var result = Assert.IsType<OkObjectResult>(await controller.Validate(request, default));
        var body = Assert.IsType<RemediationTargetValidationResponse>(result.Value);

        Assert.False(body.Valid);
        Assert.NotEmpty(body.Errors);
        Assert.Empty(_h.Db.RemediationTargets);
    }

    [Fact]
    public async Task ValidateEndpointReportsValidForAWellFormedTarget()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var controller = new RemediationTargetsController(Service());

        var result = Assert.IsType<OkObjectResult>(await controller.Validate(ValidRequest(machine, credential), default));
        var body = Assert.IsType<RemediationTargetValidationResponse>(result.Value);

        Assert.True(body.Valid);
        Assert.Empty(body.Errors);
        Assert.Empty(_h.Db.RemediationTargets); // still never persisted
    }

    // --- Phase 8: genuine, database-enforced optimistic concurrency (not just an in-memory check) ---

    [Fact]
    public async Task TwoGenuinelyConcurrentUpdatesResolveToExactlyOneWinnerAndOneCleanConflict()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        // Force both contexts to load and track the SAME row - with the SAME original UpdatedAt -
        // before either write commits. This reproduces "two requests both read before either
        // commits" (the actual race), not merely two updates run one after the other: EF's
        // identity-map behavior means UpdateAsync's own internal fetch below reuses this already-
        // tracked instance rather than resetting its recorded original value.
        await dbA.RemediationTargets.SingleAsync(t => t.Id == target.Id);
        await dbB.RemediationTargets.SingleAsync(t => t.Id == target.Id);

        var serviceA = new RemediationTargetManagementService(dbA,
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);
        var serviceB = new RemediationTargetManagementService(dbB,
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);

        UpdateRemediationTargetRequest Request(string windowsServiceName) => new()
        {
            ProjectId = target.ProjectId, Environment = target.Environment, Service = target.Service,
            MachineId = machine.Id, TelemetryCredentialId = credential.Id, ExpectedHostName = machine.HostName,
            WindowsServiceName = windowsServiceName, AllowedOperations = [ServiceToolNames.RestartService], Enabled = true
            // Deliberately no ExpectedUpdatedAt - proving the database-level concurrency token
            // protects even when the caller didn't supply the optimistic-concurrency guard itself.
        };

        var resultA = await serviceA.UpdateAsync(target.Id, Request("FromA"), "operator-a", default);
        var resultB = await serviceB.UpdateAsync(target.Id, Request("FromB"), "operator-b", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, resultA.Outcome);
        Assert.Equal(RemediationTargetOperationOutcome.Conflict, resultB.Outcome);
        Assert.Equal("stale-update", resultB.ErrorCode);
        Assert.Equal("FromA", _h.Db.RemediationTargets.AsNoTracking().Single(t => t.Id == target.Id).WindowsServiceName);
    }

    [Fact]
    public async Task TwoConcurrentEnableDisableCallsOnTheSameTargetNeverLoseAnUpdate()
    {
        var machine = _h.SeedMachine();
        var credential = _h.SeedCredential(_h.ProjectId);
        var target = _h.SeedRemediationTarget(machine.Id, credential.Id, machine.HostName);

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        await dbA.RemediationTargets.SingleAsync(t => t.Id == target.Id);
        await dbB.RemediationTargets.SingleAsync(t => t.Id == target.Id);

        var serviceA = new RemediationTargetManagementService(dbA,
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);
        var serviceB = new RemediationTargetManagementService(dbB,
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);

        var resultA = await serviceA.SetEnabledAsync(target.Id, false, "operator-a", default);
        var resultB = await serviceB.SetEnabledAsync(target.Id, false, "operator-b", default);

        Assert.Equal(RemediationTargetOperationOutcome.Success, resultA.Outcome);
        Assert.Equal(RemediationTargetOperationOutcome.Conflict, resultB.Outcome);
    }

    // --- Phase 9: a genuinely concurrent duplicate create/enable never surfaces as a raw 500 ---

    [Fact]
    public async Task ConcurrentCreatesForTheSameLogicalTargetNeverBothSucceedAndNeverThrowUnhandled()
    {
        var machine = _h.SeedMachine();
        var machine2 = _h.SeedMachine(hostName: "second-host");
        var credential = _h.SeedCredential(_h.ProjectId);

        using var dbA = _h.CreateAdditionalDbContext();
        using var dbB = _h.CreateAdditionalDbContext();
        var serviceA = new RemediationTargetManagementService(dbA,
            new PlatformAuditService(dbA, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);
        var serviceB = new RemediationTargetManagementService(dbB,
            new PlatformAuditService(dbB, TimeProvider.System, NullLogger<PlatformAuditService>.Instance), TimeProvider.System);

        // Real concurrency (not called one after the other): the database's own filtered unique
        // index - not the in-memory pre-check, which a genuine race can outrun - is what must
        // guarantee only one of these ever commits. Whichever code path catches it, the outcome
        // invariant below must hold and neither call may throw an unhandled exception.
        var taskA = serviceA.CreateAsync(ValidRequest(machine, credential), "operator-a", default);
        var taskB = serviceB.CreateAsync(ValidRequest(machine2, credential), "operator-b", default);
        var results = await Task.WhenAll(taskA, taskB);

        Assert.Contains(results, r => r.Outcome == RemediationTargetOperationOutcome.Success);
        Assert.Contains(results, r => r.Outcome == RemediationTargetOperationOutcome.Conflict);
        Assert.Single(_h.Db.RemediationTargets); // exactly one row ever committed
    }

    // --- Phase 10 (via the resolver/tool, not this service): see WindowsServiceRemediationTests
    // for the fingerprint deletion-race and sync-over-async coverage.

    private sealed class Scm : IWindowsServiceControl
    {
        public int State = 4;
        public List<(string Host, string Service, bool Start)> Calls = [];
        public Task<int> QueryAsync(string host, string service, CancellationToken ct) => Task.FromResult(State);
        public Task ChangeAsync(string host, string service, bool start, CancellationToken ct)
        {
            Calls.Add((host, service, start));
            State = start ? 4 : 1;
            return Task.CompletedTask;
        }
    }

    public void Dispose() => _h.Dispose();
}
