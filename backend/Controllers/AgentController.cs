using AIDIP.Backend.DTOs;
using AIDIP.Backend.Infrastructure;
using AIDIP.Backend.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AIDIP.Backend.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentController : ControllerBase
{
    private const string AgentKeyHeader = "X-KAIRON-Agent-Key";
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
        try { await _agents.RegisterAsync(dto, cancellationToken); }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Machine identity rejected." }); }
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

    [HttpGet("machines")]
    [RequiresOperator]
    public async Task<IReadOnlyList<MachineStatusDto>> Machines(CancellationToken cancellationToken)
    {
        var onlineAfter = _time.GetUtcNow().UtcDateTime.AddSeconds(-45);
        return await _db.Machines.AsNoTracking().OrderBy(x => x.HostName).Select(x => new MachineStatusDto(
            x.Id, x.HostName, x.OperatingSystem, x.Architecture, x.AgentVersion,
            x.LastSeenAt >= onlineAfter ? "Online" : "Offline", x.RegisteredAt, x.LastSeenAt,
            x.Applications.Count(a => a.IsRunning))).ToListAsync(cancellationToken);
    }

    [HttpGet("applications")]
    [RequiresOperator]
    public async Task<IReadOnlyList<ApplicationInventoryDto>> Applications(CancellationToken cancellationToken) =>
        await _db.DiscoveredApplications.AsNoTracking().Include(x => x.Machine)
            .OrderByDescending(x => x.IsRunning).ThenBy(x => x.Name)
            .Select(x => new ApplicationInventoryDto(x.Id, x.MachineId, x.Machine!.HostName, x.ProcessId,
                x.Name, x.Executable, x.Runtime, x.CpuPercent, x.MemoryBytes, x.IsRunning, x.LastSeenAt,
                "Basic", x.IsRunning ? "Agent telemetry" : "Stopped"))
            .ToListAsync(cancellationToken);
}
