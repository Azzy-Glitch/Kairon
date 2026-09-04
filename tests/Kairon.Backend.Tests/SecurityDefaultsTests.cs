using System.Net;
using Kairon.Backend.Configuration;
using Kairon.Backend.Infrastructure;
using Kairon.Backend.Models.Platform;
using Kairon.Backend.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// Coverage for the security-default paths this codebase previously shipped with none for:
/// SreSecurity:RequireOperatorKey / PlatformSecurity:RequireTelemetryKey enforcement. Operator
/// actions now fail closed by default; telemetry compatibility remains independently configurable.
/// Also covers the known-insecure Agent/UserAgent default credential being explicitly refused at
/// registration.
/// </summary>
public sealed class OperatorAuthorizationFilterTests
{
    [Fact]
    public void OperatorAuthorizationIsRequiredByDefault()
    {
        Assert.True(new SreSecurityOptions().RequireOperatorKey);
    }

    private static ActionExecutingContext Context(
        bool requiresOperator, string? providedHeader, string headerName, IPAddress? remoteIp = null)
    {
        var httpContext = new DefaultHttpContext();
        if (providedHeader is not null) httpContext.Request.Headers[headerName] = providedHeader;
        if (remoteIp is not null) httpContext.Connection.RemoteIpAddress = remoteIp;

        var descriptor = new ActionDescriptor
        {
            EndpointMetadata = requiresOperator ? [new RequiresOperatorAttribute()] : []
        };
        var actionContext = new ActionContext(httpContext, new RouteData(), descriptor);
        return new ActionExecutingContext(actionContext, new List<IFilterMetadata>(),
            new Dictionary<string, object?>(), controller: new object());
    }

    private static OperatorAuthorizationFilter Filter(bool require, string? operatorKey, string header = "X-Kairon-Operator-Key") =>
        new(TestHarness.Opt(new SreSecurityOptions { RequireOperatorKey = require, OperatorKey = operatorKey, HeaderName = header }),
            NullLogger<OperatorAuthorizationFilter>.Instance);

    [Fact]
    public async Task ExplicitlyDisabledAllowsThroughRegardlessOfHeader()
    {
        var context = Context(requiresOperator: true, providedHeader: null, "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: false, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task UnprotectedEndpointIsUnaffectedEvenWhenEnabled()
    {
        var context = Context(requiresOperator: false, providedHeader: null, "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: true, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task EnabledWithNoConfiguredKeyFailsClosed()
    {
        // No remote IP set up (mirrors a request whose origin this test doesn't assert on) -
        // still must not be waved through just because no key is configured.
        var context = Context(requiresOperator: true, providedHeader: "anything", "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: true, operatorKey: null).OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public async Task EnabledWithNoConfiguredKeyAllowsALocalLoopbackRequest(string loopbackIp)
    {
        // Fresh local/desktop install: SreSecurity:OperatorKey ships blank until the desktop host
        // sets one per-launch. A request that genuinely originates from this same machine must
        // still get through, so the AI Configuration panel (and anything else operator-gated) is
        // usable out of the box without a manual config edit.
        var context = Context(requiresOperator: true, providedHeader: null, "X-Kairon-Operator-Key",
            remoteIp: IPAddress.Parse(loopbackIp));
        var nextCalled = false;

        await Filter(require: true, operatorKey: null).OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }

    [Fact]
    public async Task EnabledWithNoConfiguredKeyStillFailsClosedForANonLoopbackRequest()
    {
        // Real remote/server deployments always configure a key (docker-compose.cloud.yml refuses
        // to start without one); this proves that if one somehow runs without a key configured
        // anyway, a genuinely remote request is still refused - the loopback exemption above never
        // extends to arbitrary remote callers.
        var context = Context(requiresOperator: true, providedHeader: null, "X-Kairon-Operator-Key",
            remoteIp: IPAddress.Parse("203.0.113.5"));
        var nextCalled = false;

        await Filter(require: true, operatorKey: null).OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(StatusCodes.Status401Unauthorized, result.StatusCode);
    }

    [Fact]
    public async Task ConfiguredKeyStillRejectsAWrongHeaderEvenFromLoopback()
    {
        // The loopback exemption only applies when no key is configured at all. Once a real key
        // IS configured (the actual desktop product's steady state, via its per-launch ephemeral
        // key), a wrong or missing header must still be rejected regardless of the caller's
        // address - loopback is not a blanket bypass once operator auth is actually configured.
        var context = Context(requiresOperator: true, providedHeader: "wrong-key", "X-Kairon-Operator-Key",
            remoteIp: IPAddress.Parse("127.0.0.1"));
        var nextCalled = false;

        await Filter(require: true, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task MissingHeaderIsRejected()
    {
        var context = Context(requiresOperator: true, providedHeader: null, "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: true, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, Assert.IsType<ObjectResult>(context.Result).StatusCode);
    }

    [Fact]
    public async Task WrongKeyIsRejected()
    {
        var context = Context(requiresOperator: true, providedHeader: "wrong-key", "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: true, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.False(nextCalled);
    }

    [Fact]
    public async Task CorrectKeyIsAccepted()
    {
        var context = Context(requiresOperator: true, providedHeader: "secret", "X-Kairon-Operator-Key");
        var nextCalled = false;

        await Filter(require: true, operatorKey: "secret").OnActionExecutionAsync(context, () =>
        {
            nextCalled = true;
            return Task.FromResult<ActionExecutedContext>(null!);
        });

        Assert.True(nextCalled);
        Assert.Null(context.Result);
    }
}

public sealed class TelemetryKeyEnforcementTests : IDisposable
{
    private readonly TestHarness _h = new();

    private IProjectCredentialService Service(bool require) => new ProjectCredentialService(
        _h.Db, TestHarness.Opt(new PlatformSecurityOptions { RequireTelemetryKey = require }), TimeProvider.System);

    [Fact]
    public async Task DisabledKeyRequirementStillRejectsAnUnregisteredProject()
    {
        var authorized = await Service(require: false).AuthorizeAsync(Guid.NewGuid(), null, default);
        Assert.False(authorized);
    }

    [Fact]
    public async Task DisabledKeyRequirementAllowsARegisteredActiveProjectWithoutAKey()
    {
        var project = new Project { Name = "test" };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        Assert.True(await Service(require: false).AuthorizeAsync(project.Id, null, default));
    }

    [Fact]
    public async Task InactiveProjectIsRejectedEvenWhenKeysAreOptional()
    {
        var project = new Project { Name = "test", IsActive = false };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        Assert.False(await Service(require: false).AuthorizeAsync(project.Id, null, default));
    }

    [Fact]
    public async Task EnabledWithNoCredentialIssuedRejectsEverything()
    {
        var project = new Project { Name = "test" };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        var authorized = await Service(require: true).AuthorizeAsync(project.Id, "any-key-at-all-1234", default);
        Assert.False(authorized);
    }

    [Fact]
    public async Task EnabledWithAValidIssuedCredentialIsAccepted()
    {
        var project = new Project { Name = "test" };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        var service = Service(require: true);
        var created = await service.CreateAsync(project.Id, "Telemetry", default);

        Assert.True(await service.AuthorizeAsync(project.Id, created!.ApiKey, default));
    }

    [Fact]
    public async Task EnabledWithARevokedCredentialIsRejected()
    {
        var project = new Project { Name = "test" };
        _h.Db.Projects.Add(project);
        _h.Db.SaveChanges();

        var service = Service(require: true);
        var created = await service.CreateAsync(project.Id, "Telemetry", default);
        await service.RevokeAsync(project.Id, created!.Id, default);

        Assert.False(await service.AuthorizeAsync(project.Id, created.ApiKey, default));
    }

    public void Dispose() => _h.Dispose();
}
