using System.Net;
using System.Reflection;
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
/// The machine-enrollment trust boundary. POST /api/agent/register is authorized by its OWN
/// credential (AgentEnrollmentSecurity:EnrollmentKeys), not by the operator key, and these tests
/// drive the real AgentEnrollmentAuthorizationFilter composed with the real AgentController action
/// exactly as MVC would - so what is proven is that registration is actually blocked or allowed,
/// not merely that an attribute is present.
///
/// The cross-boundary half matters as much as the direct half: an enrollment key must not open an
/// operator endpoint, and an operator key must not enroll a machine. Both directions are asserted
/// below against the real filters.
/// </summary>
public sealed class AgentEnrollmentAuthorizationTests : IDisposable
{
    private const string EnrollmentHeader = "X-Kairon-Enrollment-Key";
    private const string OperatorHeader = "X-Kairon-Operator-Key";

    private readonly TestHarness _h = new();

    // --- Direct boundary ------------------------------------------------------------------

    [Fact]
    public async Task RemoteRegistrationWithNoEnrollmentKeyIsRejectedBeforeReachingTheService()
    {
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"], headers: [],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task WrongEnrollmentKeyIsRejected()
    {
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"],
            headers: [(EnrollmentHeader, "not-the-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task CorrectEnrollmentKeyFromARemoteOriginIsAccepted()
    {
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"],
            headers: [(EnrollmentHeader, "enrollment-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(machineExists);
    }

    [Fact]
    public async Task EitherKeyIsAcceptedDuringARotationWindow()
    {
        // Rotation: the new key is published alongside the previous one, the fleet moves over,
        // then the old entry is dropped. Both must work while both are configured.
        var (newStatus, newExists) = await RegisterAsync(
            configuredKeys: ["previous-secret", "current-secret"],
            headers: [(EnrollmentHeader, "current-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"));
        var (oldStatus, oldExists) = await RegisterAsync(
            configuredKeys: ["previous-secret", "current-secret"],
            headers: [(EnrollmentHeader, "previous-secret")],
            remoteIp: IPAddress.Parse("203.0.113.6"));

        Assert.Equal(StatusCodes.Status200OK, newStatus);
        Assert.True(newExists);
        Assert.Equal(StatusCodes.Status200OK, oldStatus);
        Assert.True(oldExists);
    }

    [Fact]
    public async Task LocalSingleMachineInstallEnrollsWithNoEnrollmentKeyConfigured()
    {
        // The packaged desktop: nothing configured, Agent and backend on the same machine. This is
        // the case the operator key could never have served - the desktop mints a fresh operator
        // key per launch and gives it only to the backend, so the Agent had no way to present one.
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: [], headers: [], remoteIp: IPAddress.Loopback);

        Assert.Equal(StatusCodes.Status200OK, status);
        Assert.True(machineExists);
    }

    [Fact]
    public async Task ARemoteCallerIsRejectedWhenNoEnrollmentKeyIsConfigured()
    {
        // The flip side: an unconfigured backend still must not trust a registration attempt that
        // did not originate from this machine.
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: [], headers: [], remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task ABlankConfiguredEntryNeverAuthorizesAnEmptyOrAbsentHeader()
    {
        // A half-filled config array ("EnrollmentKeys": [""]) must not degrade into "any request
        // with no key matches the configured empty key".
        var (absent, absentExists) = await RegisterAsync(
            configuredKeys: ["", "  "], headers: [], remoteIp: IPAddress.Parse("203.0.113.5"));
        var (empty, emptyExists) = await RegisterAsync(
            configuredKeys: ["", "  "], headers: [(EnrollmentHeader, "")],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, absent);
        Assert.False(absentExists);
        Assert.Equal(StatusCodes.Status401Unauthorized, empty);
        Assert.False(emptyExists);
    }

    [Fact]
    public async Task ReRegistrationOfAnExistingMachineIsGatedTheSameWay()
    {
        var machineId = Guid.NewGuid();
        await RegisterAsync(
            configuredKeys: ["enrollment-secret"],
            headers: [(EnrollmentHeader, "enrollment-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"), machineId: machineId);

        // Rotation/re-registration is not a lesser-protected path than first enrollment.
        var (status, _) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"], headers: [],
            remoteIp: IPAddress.Parse("203.0.113.5"), machineId: machineId);

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
    }

    // --- Cross boundary: neither credential may satisfy the other -------------------------

    [Fact]
    public async Task AnOperatorKeyAloneCannotEnrollAMachine()
    {
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"],
            headers: [(OperatorHeader, "operator-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Fact]
    public async Task AnOperatorKeySentInTheEnrollmentHeaderIsStillJustAWrongKey()
    {
        // Guards against the two secrets ever being made interchangeable by configuration.
        var (status, machineExists) = await RegisterAsync(
            configuredKeys: ["enrollment-secret"],
            headers: [(EnrollmentHeader, "operator-secret")],
            remoteIp: IPAddress.Parse("203.0.113.5"));

        Assert.Equal(StatusCodes.Status401Unauthorized, status);
        Assert.False(machineExists);
    }

    [Theory]
    [InlineData(EnrollmentHeader)]
    [InlineData(OperatorHeader)]
    public async Task AnEnrollmentKeyNeverReachesAnOperatorAction(string header)
    {
        // The real operator filter, a real [RequiresOperator] endpoint's metadata, and a caller
        // holding only the enrollment secret - in either header. It must be refused both ways.
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        httpContext.Request.Headers[header] = "enrollment-secret";

        var executingContext = ExecutingContext(httpContext, new RequiresOperatorAttribute());
        var filter = new OperatorAuthorizationFilter(
            TestHarness.Opt(new SreSecurityOptions
            {
                RequireOperatorKey = true,
                OperatorKey = "operator-secret",
                HeaderName = OperatorHeader
            }),
            NullLogger<OperatorAuthorizationFilter>.Instance);

        var reached = false;
        await filter.OnActionExecutionAsync(executingContext, () =>
        {
            reached = true;
            return Task.FromResult(ExecutedContext(executingContext));
        });

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            (executingContext.Result as IStatusCodeActionResult)?.StatusCode);
    }

    [Fact]
    public async Task TheEnrollmentFilterIgnoresEveryEndpointThatIsNotMarkedForEnrollment()
    {
        // The filter is registered globally. An action carrying no enrollment attribute must pass
        // straight through it regardless of headers or origin, so adding this boundary cannot
        // silently gate unrelated endpoints.
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");

        var executingContext = ExecutingContext(httpContext);
        var reached = false;

        await EnrollmentFilter(["enrollment-secret"]).OnActionExecutionAsync(executingContext, () =>
        {
            reached = true;
            return Task.FromResult(ExecutedContext(executingContext));
        });

        Assert.True(reached);
        Assert.Null(executingContext.Result);
    }

    [Fact]
    public void RegisterIsGatedByEnrollmentAndNoEndpointCarriesBothBoundaries()
    {
        var register = typeof(AgentController).GetMethod(nameof(AgentController.Register))!;
        Assert.NotNull(register.GetCustomAttribute<RequiresAgentEnrollmentAttribute>());
        Assert.Null(register.GetCustomAttribute<RequiresOperatorAttribute>());

        // Enrollment marks exactly one endpoint in the whole product, and never together with the
        // operator boundary: the two authorize different privileges and must not be combined.
        var marked = typeof(AgentController).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m => m.GetCustomAttribute<RequiresAgentEnrollmentAttribute>() is not null)
            .ToList();

        Assert.Equal(new[] { register }, marked);
        Assert.All(marked, m => Assert.Null(m.GetCustomAttribute<RequiresOperatorAttribute>()));
        Assert.All(marked, m => Assert.Null(m.DeclaringType!.GetCustomAttribute<RequiresOperatorAttribute>()));
    }

    [Fact]
    public async Task ARejectedEnrollmentKeyIsNeverEchoedBackToTheCaller()
    {
        const string secret = "a-secret-that-must-not-appear-in-any-response";
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");
        httpContext.Request.Headers[EnrollmentHeader] = secret;

        var executingContext = ExecutingContext(httpContext, new RequiresAgentEnrollmentAttribute());
        await EnrollmentFilter(["the-real-secret"]).OnActionExecutionAsync(
            executingContext, () => Task.FromResult(ExecutedContext(executingContext)));

        var body = System.Text.Json.JsonSerializer.Serialize(
            (executingContext.Result as ObjectResult)?.Value);

        Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the-real-secret", body, StringComparison.OrdinalIgnoreCase);
    }

    // --- Harness --------------------------------------------------------------------------

    private static AgentEnrollmentAuthorizationFilter EnrollmentFilter(string[] configuredKeys) =>
        new(TestHarness.Opt(new AgentEnrollmentSecurityOptions
        {
            RequireEnrollmentKey = true,
            HeaderName = EnrollmentHeader,
            EnrollmentKeys = configuredKeys.ToList()
        }),
        NullLogger<AgentEnrollmentAuthorizationFilter>.Instance);

    private static ActionExecutingContext ExecutingContext(HttpContext httpContext, params object[] metadata)
    {
        var descriptor = new ActionDescriptor { EndpointMetadata = metadata };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), controller: new object());
    }

    private static ActionExecutedContext ExecutedContext(ActionExecutingContext executing) =>
        new(executing, new List<IFilterMetadata>(), controller: new object());

    private async Task<(int Status, bool MachineExists)> RegisterAsync(
        string[] configuredKeys, (string Name, string Value)[] headers, IPAddress remoteIp,
        Guid? machineId = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Connection.RemoteIpAddress = remoteIp;
        foreach (var (name, value) in headers) httpContext.Request.Headers[name] = value;

        var executingContext = ExecutingContext(httpContext, new RequiresAgentEnrollmentAttribute());
        var controller = new AgentController(
            new AgentRegistrationService(_h.Db, TimeProvider.System), _h.Db, TimeProvider.System);
        var id = machineId ?? Guid.NewGuid();
        IActionResult? actionResult = null;

        await EnrollmentFilter(configuredKeys).OnActionExecutionAsync(executingContext, async () =>
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
            return new ActionExecutedContext(executingContext, new List<IFilterMetadata>(), controller: new object())
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
