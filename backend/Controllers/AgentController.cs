using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kairon.Backend.Controllers;

/// <summary>
/// Machine/process discovery ("Basic Monitoring", docs/DESKTOP_SHELL.md) - the KAIRON Agent
/// registers its machine identity once, then heartbeats the processes its own ProcessWatcher
/// currently sees. Distinct from TelemetryController's log/process EVENT ingestion (AgentEvent) -
/// this is machine/process INVENTORY, a read-only "what's running where" list, not incident
/// signals.
/// </summary>
[ApiController]
[Route("api/agent")]
public sealed class AgentController : ControllerBase
{
    private const string AgentKeyHeader = "X-Kairon-Agent-Key";
    private readonly IAgentRegistrationService _agents;
    private readonly AppDbContext _db;
    private readonly TimeProvider _time;

    public AgentController(IAgentRegistrationService agents, AppDbContext db, TimeProvider time)
    {
        _agents = agents;
        _db = db;
        _time = time;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register(AgentRegistrationDto dto, CancellationToken cancellationToken)
    {
        try
        {
            await _agents.RegisterAsync(dto, cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized(new { error = "Machine identity rejected." });
        }

        return Ok(new { machineId = dto.MachineId, registered = true });
    }

    [HttpPost("machines/{machineId:guid}/heartbeat")]
    public async Task<IActionResult> Heartbeat(Guid machineId, AgentHeartbeatDto dto, CancellationToken cancellationToken)
    {
        var key = Request.Headers[AgentKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) || !await _agents.RecordHeartbeatAsync(machineId, key, dto, cancellationToken))
            return Unauthorized(new { error = "Agent authentication failed." });

        return Ok(new { accepted = true, applications = dto.Processes.Count });
    }

    /// <summary>Heartbeat from KAIRON.UserAgent - the per-interactive-session counterpart to
    /// <see cref="Heartbeat"/>, authenticated with its distinct scoped key against the same Machine
    /// identity the Windows Service already registered (docs/DESKTOP_SHELL.md). It does not affect the
    /// machine's own online/offline status - see AgentRegistrationService.</summary>
    [HttpPost("machines/{machineId:guid}/user-session/heartbeat")]
    public async Task<IActionResult> UserSessionHeartbeat(Guid machineId, UserSessionHeartbeatDto dto,
        CancellationToken cancellationToken)
    {
        var key = Request.Headers[AgentKeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(key) ||
            !await _agents.RecordUserSessionHeartbeatAsync(machineId, key, dto, cancellationToken))
            return Unauthorized(new { error = "Agent authentication failed." });

        return Ok(new { accepted = true, applications = dto.Processes.Count });
    }

    [HttpGet("machines")]
    [RequiresOperator]
    public async Task<IReadOnlyList<MachineStatusDto>> Machines(CancellationToken cancellationToken)
    {
        var onlineAfter = _time.GetUtcNow().UtcDateTime.AddSeconds(-45);
        return await _db.Machines.AsNoTracking()
            .OrderBy(x => x.HostName)
            .Select(x => new MachineStatusDto(x.Id, x.HostName, x.OperatingSystem, x.Architecture, x.AgentVersion,
                x.LastSeenAt >= onlineAfter ? "Online" : "Offline", x.RegisteredAt, x.LastSeenAt,
                _db.DiscoveredApplications.Count(a => a.MachineId == x.Id && a.IsRunning),
                x.LastUserAgentSeenAt == null ? "NeverConnected"
                    : x.LastUserAgentSeenAt >= onlineAfter ? "Online" : "Offline",
                x.LastUserAgentSeenAt))
            .ToListAsync(cancellationToken);
    }

    [HttpGet("applications")]
    [RequiresOperator]
    public async Task<IReadOnlyList<ApplicationInventoryDto>> Applications(CancellationToken cancellationToken) =>
        await (
            from application in _db.DiscoveredApplications.AsNoTracking()
            join machine in _db.Machines.AsNoTracking() on application.MachineId equals machine.Id
            orderby application.IsRunning descending, application.Name
            select new ApplicationInventoryDto(application.Id, application.MachineId, machine.HostName,
                application.ProcessId, application.Name, application.Executable, application.Runtime,
                application.CpuPercent, application.MemoryBytes, application.IsRunning, application.LastSeenAt,
                application.Source, application.ParentProcessId, application.SessionId, application.UserName)
        ).ToListAsync(cancellationToken);
}
