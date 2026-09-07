using Kairon.Backend.Services.Remediation;
using Kairon.Backend.Configuration;
using Kairon.Backend.Models.Sre;
using Kairon.Backend.Services.Orchestration;
using Kairon.Backend.Services.Remediation.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class RemediationRecoveryTests
{
    private static (SreIncident, RemediationAction) Seed(TestHarness h, RemediationStatus state)
    {
        var incident = h.SeedIncident(state == RemediationStatus.Executed ? IncidentStatus.Verifying : IncidentStatus.Remediating);
        var action = new RemediationAction { IncidentId = incident.Id, ActionKey = "ACT-recovery", ActionType = DemoToolNames.DisableDemoRetryLoop,
            Status = state, ApprovedBy = "operator", ApprovedAt = DateTime.UtcNow, RiskLevel = RiskLevel.Low,
            StartedAt = state == RemediationStatus.Approved ? null : DateTime.UtcNow.AddSeconds(-10),
            CompletedAt = state == RemediationStatus.Executed ? DateTime.UtcNow.AddSeconds(-5) : null };
        incident.Actions.Add(action); h.Db.RemediationActions.Add(action); h.Db.SaveChanges(); return (incident, action);
    }
    private static IncidentProcessingQueue NewQueue(int size = 512) => new(Options.Create(new AiOrchestrationOptions { QueueCapacity = size }), NullLogger<IncidentProcessingQueue>.Instance);
    private static void RecoveryMetrics(TestHarness h)
    {
        h.SeedMetric(DateTime.UtcNow.AddSeconds(-30), cpu:95, retries:50);
        h.BeforeVerification = () => h.SeedMetric(DateTime.UtcNow, cpu:10, retries:0);
    }

    [Fact]
    public async Task LostQueueRecoversUnstartedApprovalAndDuplicateDeliveryDoesNotRepeatCommand()
    {
        using var h = new TestHarness(); var (i,a) = Seed(h, RemediationStatus.Approved); RecoveryMetrics(h);
        var queue = NewQueue(); // A fresh process has no delivery memory, only persisted approval.
        var recovery = new RemediationRecoveryService(h.Db, queue, h.Audit);
        await recovery.ReconcileAsync(); await recovery.ReconcileAsync(); Assert.Equal(1, queue.Count);
        h.Demo.Delay = TimeSpan.FromMilliseconds(20);
        await Task.WhenAll(h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id), h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id));
        Assert.Single(h.Demo.Commands); Assert.Equal(IncidentStatus.Resolved, i.Status);
        await recovery.ReconcileAsync(); Assert.Single(h.Db.IncidentEvents.Where(e=>e.EventType=="RemediationRecoveryQueued"));
    }
    [Theory]
    [InlineData(RemediationStatus.Executing)]
    [InlineData(RemediationStatus.Approved)]
    public async Task InterruptedSideEffectIsNeverAutomaticallyRepeated(RemediationStatus state)
    {
        using var h = new TestHarness();var (i,a)=Seed(h,state);a.StartedAt=DateTime.UtcNow;h.Db.SaveChanges();
        var recovery=new RemediationRecoveryService(h.Db,NewQueue(),h.Audit);
        await recovery.ReconcileAsync(); await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id);await recovery.ReconcileAsync();
        Assert.Empty(h.Demo.Commands);Assert.Equal(IncidentStatus.Failed,i.Status);
        Assert.Contains("uncertain",i.FailureReason);Assert.Single(h.Db.IncidentEvents.Where(e=>e.EventType=="RemediationRecoveryFailed"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedExecutionResumesOnlyVerification(bool alreadyVerifying)
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Executed);RecoveryMetrics(h);
        if(!alreadyVerifying)i.Status=IncidentStatus.Remediating;
        var pending=new VerificationResult {IncidentId=i.Id,ActionId=a.Id,Status=VerificationStatus.Pending};i.Verifications.Add(pending);h.Db.VerificationResults.Add(pending);h.Db.SaveChanges();
        var queue=NewQueue();await new RemediationRecoveryService(h.Db,queue,h.Audit).ReconcileAsync();Assert.Equal(1,queue.Count);
        await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id);
        Assert.Empty(h.Demo.Commands);Assert.Equal(IncidentStatus.Resolved,i.Status);Assert.Equal(VerificationStatus.Inconclusive,pending.Status);
        Assert.Single(h.Db.IncidentEvents.Where(e=>e.EventType==RemediationRecoveryService.VerificationResumed));
    }
    [Fact]
    public async Task ASecondInterruptedVerificationFailsWithoutReplay()
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Executed);
        h.Audit.Record(i,RemediationRecoveryService.VerificationResumed,"test",actionId:a.ActionKey);h.Db.SaveChanges();
        var q=NewQueue();await new RemediationRecoveryService(h.Db,q,h.Audit).ReconcileAsync();
        Assert.Equal(0,q.Count);Assert.Equal(IncidentStatus.Failed,i.Status);Assert.Empty(h.Demo.Commands);
        Assert.Equal(RemediationStatus.Executed,a.Status);Assert.Contains("exhausted",i.FailureReason);
    }
    [Fact]
    public async Task ActiveExecutionIsNotMistakenForAnInterruptedProcess()
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Executing);
        using(var lease=await RemediationExecutionGate.EnterAsync(i.Id,default))
        {
            await new RemediationRecoveryService(h.Db,NewQueue(),h.Audit).ReconcileAsync();
            Assert.Equal(IncidentStatus.Remediating,i.Status);
        }
        await new RemediationRecoveryService(h.Db,NewQueue(),h.Audit).ReconcileAsync();
        Assert.Equal(IncidentStatus.Failed,i.Status);
    }
    [Fact]
    public async Task FullApprovalQueueIsAuditedAndRecoveredAfterCapacityReturns()
    {
        using var h=new TestHarness(x=>x.AiOptions.InvestigationDelaySeconds=0);
        var i=h.SeedIncident();await h.CreateOrchestrator().InvestigateAsync(i.Id);var a=i.Actions.Single();
        while(h.Queue.TryEnqueue(new IncidentWorkItem(WorkItemKind.EvaluateDetection,Guid.NewGuid(),"Production","test"))){}
        await h.CreateOrchestrator().ApproveAsync(i.Id,a.Id,"operator",null);
        Assert.Equal(RemediationStatus.Approved,a.Status);Assert.Contains(h.Db.IncidentEvents,e=>e.EventType=="RemediationDeferred");
        var fresh=NewQueue();await new RemediationRecoveryService(h.Db,fresh,h.Audit).ReconcileAsync();Assert.Equal(1,fresh.Count);
    }
    [Fact]
    public async Task ContinuedQueueRejectionRemainsBoundedByOriginalApprovalAge()
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Approved);
        var q=NewQueue(1);q.TryEnqueue(new IncidentWorkItem(WorkItemKind.EvaluateDetection,Guid.NewGuid(),"Production","busy"));
        var recovery=new RemediationRecoveryService(h.Db,q,h.Audit);await recovery.ReconcileAsync();Assert.Equal(RemediationStatus.Approved,a.Status);
        a.ApprovedAt=DateTime.UtcNow-RemediationRecoveryService.RecoveryWindow-TimeSpan.FromSeconds(1);h.Db.SaveChanges();
        await recovery.ReconcileAsync();Assert.Equal(IncidentStatus.Failed,i.Status);Assert.Empty(h.Demo.Commands);
    }
    [Fact]
    public async Task PolicyIsRecheckedForRecoveredUnstartedWork()
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Approved);h.Remediation.Enabled=false;
        await new RemediationRecoveryService(h.Db,NewQueue(),h.Audit).ReconcileAsync();await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id);
        Assert.Empty(h.Demo.Commands);Assert.Equal(IncidentStatus.Failed,i.Status);Assert.Equal(RemediationStatus.PolicyRejected,a.Status);
    }
    [Theory]
    [InlineData(IncidentStatus.Remediating)]
    [InlineData(IncidentStatus.Verifying)]
    public async Task MissingRecoverableActionDoesNotLeaveIncidentStranded(IncidentStatus status)
    {
        using var h=new TestHarness();var i=h.SeedIncident(status);
        await new RemediationRecoveryService(h.Db,NewQueue(),h.Audit).ReconcileAsync();Assert.Equal(IncidentStatus.Failed,i.Status);
    }
    [Theory]
    [InlineData(RemediationStatus.Approved, 1)]
    [InlineData(RemediationStatus.Executed, 1)]
    [InlineData(RemediationStatus.Executing, 0)]
    public async Task RestartRecoveryReadsDurableStateInAFreshDbContext(RemediationStatus status, int expectedQueueCount)
    {
        using var h=new TestHarness();var(i,a)=Seed(h,status);
        await using var fresh=new Kairon.Backend.Infrastructure.AppDbContext(
            new DbContextOptionsBuilder<Kairon.Backend.Infrastructure.AppDbContext>().UseSqlite(h.Db.Database.GetDbConnection()).Options);
        var audit=new Kairon.Backend.Services.Audit.AuditService(fresh,NullLogger<Kairon.Backend.Services.Audit.AuditService>.Instance);
        var q=NewQueue();await new RemediationRecoveryService(fresh,q,audit).ReconcileAsync();
        Assert.Equal(expectedQueueCount,q.Count);Assert.Empty(h.Demo.Commands);
        if(status==RemediationStatus.Executing) Assert.Equal(IncidentStatus.Failed,(await fresh.SreIncidents.SingleAsync()).Status);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerificationExceptionIsRecoveredOnceWithoutRepeatingExecution(bool repeatedFailure)
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Approved);RecoveryMetrics(h);
        var healthy=h.BeforeVerification;h.BeforeVerification=()=>throw new InvalidOperationException("injected verifier interruption");
        await Assert.ThrowsAsync<InvalidOperationException>(()=>h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id));
        Assert.Equal(RemediationStatus.Executed,a.Status);Assert.Single(h.Demo.Commands);
        var q=NewQueue();var recovery=new RemediationRecoveryService(h.Db,q,h.Audit);await recovery.ReconcileAsync();Assert.Equal(1,q.Count);
        if(!repeatedFailure)h.BeforeVerification=healthy;
        if(repeatedFailure)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(()=>h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id));
            await recovery.ReconcileAsync();Assert.Equal(IncidentStatus.Failed,i.Status);
        }
        else {await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id);Assert.Equal(IncidentStatus.Resolved,i.Status);}
        Assert.Single(h.Demo.Commands);
    }
    [Fact]
    public async Task ExpiredQueueDeliveryCannotBeatTheRecoverySweepAndExecute()
    {
        using var h=new TestHarness();var(i,a)=Seed(h,RemediationStatus.Approved);
        a.ApprovedAt=DateTime.UtcNow-RemediationRecoveryService.RecoveryWindow-TimeSpan.FromSeconds(1);h.Db.SaveChanges();
        await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,a.Id);Assert.Empty(h.Demo.Commands);
        await new RemediationRecoveryService(h.Db,NewQueue(),h.Audit).ReconcileAsync();Assert.Equal(IncidentStatus.Failed,i.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EarlierExecutedAlternativeDoesNotReplaceTheCurrentAttempt(bool latestFailed)
    {
        using var h=new TestHarness();var(i,old)=Seed(h,RemediationStatus.Executed);RecoveryMetrics(h);
        var v=new VerificationResult {IncidentId=i.Id,ActionId=old.Id,Status=VerificationStatus.Failed,CompletedAt=DateTime.UtcNow};
        i.Verifications.Add(v);h.Db.VerificationResults.Add(v);old.VerificationResultId=v.Id;
        i.Status=IncidentStatus.Remediating;
        var current=new RemediationAction {IncidentId=i.Id,ActionKey="ACT-alternative",ActionType=old.ActionType,Status=RemediationStatus.Approved,
            ApprovedBy="operator",ApprovedAt=DateTime.UtcNow,RiskLevel=RiskLevel.Low};
        if (latestFailed) { current.Status=RemediationStatus.Failed; current.ExecutionError="injected failed outcome";current.CompletedAt=DateTime.UtcNow; }
        i.Actions.Add(current);h.Db.RemediationActions.Add(current);h.Db.SaveChanges();
        var q=NewQueue();await new RemediationRecoveryService(h.Db,q,h.Audit).ReconcileAsync();
        if (latestFailed) { Assert.Equal(0,q.Count);Assert.Equal(IncidentStatus.Failed,i.Status);Assert.Empty(h.Demo.Commands);return; }
        Assert.Equal(1,q.Count);Assert.Equal(IncidentStatus.Remediating,i.Status);
        await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,old.Id); // Old delivery must not resume an obsolete verification.
        Assert.Empty(h.Demo.Commands);Assert.Equal(RemediationStatus.Approved,current.Status);
        Assert.Single(i.Verifications);
        await h.CreateOrchestrator().ExecuteAndVerifyAsync(i.Id,current.Id);
        Assert.Equal(IncidentStatus.Resolved,i.Status);Assert.Single(h.Demo.Commands);
        Assert.Equal(VerificationStatus.Failed,v.Status); // Historical evidence is preserved.
    }

}
