using AIDIP.Backend.Models.Sre;

namespace AIDIP.Backend.Services.Remediation;

/// <summary>
/// A strongly typed, registered remediation capability (PRD section 11).
///
/// This interface is the security boundary from PRD section 12. There is no tool that takes a
/// command string, a script, a file path, or a SQL statement; the only things that can ever run
/// are the implementations registered in <see cref="IRemediationToolRegistry"/>, each with a fixed
/// effect. The AI selects a tool by name - it never supplies behaviour.
/// </summary>
public interface IRemediationTool
{
    /// <summary>Stable name the AI must use to select this tool. Must match exactly.</summary>
    string Name { get; }

    string Description { get; }

    /// <summary>Inherent risk, used by policy to decide whether the action is permitted at all.</summary>
    RiskLevel RiskLevel { get; }

    /// <summary>
    /// Metrics this tool is expected to move, used by verification to decide what "it worked"
    /// means for this action rather than checking everything blindly.
    /// </summary>
    IReadOnlyList<string> ExpectedMetricEffects { get; }

    /// <summary>
    /// Validates the parameters the action carries. Returning false stops the action before any
    /// side effect occurs.
    /// </summary>
    bool ValidateParameters(IReadOnlyDictionary<string, string> parameters, out string? error);

    Task<RemediationToolResult> ExecuteAsync(RemediationToolContext context, CancellationToken cancellationToken = default);
}

/// <summary>What a tool is allowed to know about the incident it is fixing.</summary>
public class RemediationToolContext
{
    public required Guid IncidentId { get; init; }
    public required string IncidentKey { get; init; }
    public required string Service { get; init; }
    public required string Environment { get; init; }
    public required string ActionKey { get; init; }
    public IReadOnlyDictionary<string, string> Parameters { get; init; } = new Dictionary<string, string>();
}

public class RemediationToolResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public string? Error { get; init; }
    public Dictionary<string, string> Details { get; init; } = new();

    public static RemediationToolResult Ok(string message, Dictionary<string, string>? details = null) =>
        new() { Success = true, Message = message, Details = details ?? new Dictionary<string, string>() };

    public static RemediationToolResult Fail(string error) =>
        new() { Success = false, Message = "Remediation tool reported failure", Error = error };
}

/// <summary>
/// The registry of executable tools. Nothing outside this registry can ever execute - that is the
/// whole point of the security invariant in PRD section 12.
/// </summary>
public interface IRemediationToolRegistry
{
    IReadOnlyList<IRemediationTool> All();
    bool TryGet(string name, out IRemediationTool tool);
    bool Contains(string name);
}

public class RemediationToolRegistry : IRemediationToolRegistry
{
    private readonly Dictionary<string, IRemediationTool> _tools;

    public RemediationToolRegistry(IEnumerable<IRemediationTool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IRemediationTool> All() => _tools.Values.OrderBy(t => t.Name).ToList();

    public bool TryGet(string name, out IRemediationTool tool)
    {
        if (!string.IsNullOrWhiteSpace(name) && _tools.TryGetValue(name.Trim(), out var found))
        {
            tool = found;
            return true;
        }

        tool = null!;
        return false;
    }

    public bool Contains(string name) => TryGet(name, out _);
}

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
