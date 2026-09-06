namespace Kairon.Backend.Services.Remediation;

/// <summary>Canonical demo tool names (PRD section 11), so the AI mock and the registry agree.</summary>
public static class DemoToolNames
{
    public const string RestartDemoService = "RestartDemoService";
    public const string ClearDemoCache = "ClearDemoCache";
    public const string DisableDemoRetryLoop = "DisableDemoRetryLoop";
    public const string ReduceDemoWorkerConcurrency = "ReduceDemoWorkerConcurrency";
    public const string ResetDemoFailureSimulation = "ResetDemoFailureSimulation";
    public const string RunHealthCheck = "RunHealthCheck";
}
