using System.Net;
using Kairon.Backend.Configuration;
using Kairon.Backend.Controllers;
using Kairon.Backend.DTOs;
using Kairon.Backend.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// RB-005: end-to-end proof that POST /api/agent/register genuinely goes through the same
/// operator-authorization boundary as every other RequiresOperator action - not merely that the
/// attribute is present (ProtectedReadEndpointTests already checks that), but that the real
/// OperatorAuthorizationFilter, composed with the real AgentController action exactly as ASP.NET
/// Core's MVC pipeline would, actually blocks/allows registration accordingly.
/// </summary>
public sealed class AgentEnrollmentAuthorizationTests : IDisposable
{
    private readonly TestHarness _h = new();

    [Fact]
    public async Task UnauthorizedRemoteRegistrationIsRejectedBeforeReachingTheService()
    {
        var (status, machineExists) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: "operator-secret", providedKey: null,
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task WrongOperatorKeyIsRejected()
    {
        var (status, machineExists) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: "operator-secret", providedKey: "not-the-secret",
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task CorrectOperatorKeyFromARemoteOriginIsAccepted()
    {
        var (status, machineExists) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: "operator-secret", providedKey: "operator-secret",
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(machineExists);
    }

    [Fact]
    public async Task LocalSingleMachineInstallWorksWithNoOperatorKeyConfigured()
    {
        // The default, unconfigured desktop scenario: no SreSecurity:OperatorKey set at all, and
        // the registration request originates from this same machine (loopback) - must succeed
        // with zero operator involvement.
        var (status, machineExists) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: null, providedKey: null,
            remoteIp: IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(machineExists);
    }

    [Fact]
    public async Task ARemoteCallerIsRejectedWhenNoOperatorKeyIsConfigured()
    {
        // The flip side of the previous test: an unconfigured backend still must not trust a
        // registration attempt that did NOT originate from this machine.
        var (status, machineExists) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: null, providedKey: null,
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task ReRegistrationOfAnExistingMachineIsGatedTheSameWay()
    {
        // First, authorized registration establishes the machine.
        var machineId = Guid.NewGuid();
        await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: "operator-secret", providedKey: "operator-secret",
            remoteIp: IPAddress.Parse("203.0.113.5"), machineId: machineId);

        // An unauthorized re-registration attempt for the SAME machine is rejected identically -
        // rotation is not a lesser-protected path than first-time enrollment.
        var (status, _) = await InvokeRegisterThroughFilterAsync(
            requireOperatorKey: true, configuredKey: "operator-secret", providedKey: null,
            remoteIp: IPAddress.Parse("203.0.113.5"), machineId: machineId);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    private async Task<(int Status, bool MachineExists)> InvokeRegisterThroughFilterAsync(
        bool requireOperatorKey, string? configuredKey, string? providedKey, IPAddress remoteIp,
        Guid? machineId = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = remoteIp;
        if (providedKey is not null) httpContext.Request.Headers["X-Kairon-Operator-Key"] = providedKey;

        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = [new RequiresOperatorAttribute()]
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        var executingContext = new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), controller: new object());

        var filter = new OperatorAuthorizationFilter(
            TestHarness.Opt(new SreSecurityOptions
            {
                RequireOperatorKey = requireOperatorKey,
                OperatorKey = configuredKey,
                HeaderName = "X-Kairon-Operator-Key"
            }),
            NullLogger<OperatorAuthorizationFilter>.Instance);

        var controller = new AgentController(new AgentRegistrationService(_h.Db, TimeProvider.System), _h.Db, TimeProvider.System);
        var id = machineId ?? Guid.NewGuid();
        IActionResult? actionResult = null;

        await filter.OnActionExecutionAsync(executingContext, async () =>
        {
            actionResult = await controller.Register(new AgentRegistrationDto
            {
                MachineId = id,
                HostName = "host",
                OperatingSystem = "Windows",
                Architecture = "X64",
                AgentVersion = "1.0",
                AgentKey = "a-genuinely-random-generated-agent-key-1234567890",
                UserAgentKey = "a-genuinely-random-generated-useragent-key-1234567890"
            }, CancellationToken.None);
            return new ActionExecutedContext(actionContext, new List<IFilterMetadata>(), controller: new object())
            {
                Result = actionResult
            };
        });

        var status = (executingContext.Result as IStatusCodeActionResult)?.StatusCode
            ?? (actionResult as IStatusCodeActionResult)?.StatusCode
            ?? StatusCodes.Status200OK;

        var exists = await _h.Db.Machines.AnyAsync(m => m.Id == id);
        return (status, exists);
    }

    public void Dispose() => _h.Dispose();
}
