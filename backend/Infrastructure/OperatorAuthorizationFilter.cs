using System.Net;
using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// Marks an endpoint that changes remediation state - approve, reject, cancel, execute. When
/// SreSecurity:RequireOperatorKey is on, these require a server-side key (PRD section 19).
///
/// Enabled by default. The desktop host supplies a fresh per-launch key without exposing it to
/// JavaScript; non-desktop deployments must explicitly configure their own key. If no key is
/// configured at all, only requests from this same machine are let through - see
/// <see cref="OperatorAuthorizationFilter"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresOperatorAttribute : Attribute
{
}

public class OperatorAuthorizationFilter : IAsyncActionFilter
{
    private readonly SreSecurityOptions _options;
    private readonly ILogger<OperatorAuthorizationFilter> _logger;

    public OperatorAuthorizationFilter(
        IOptions<SreSecurityOptions> options,
        ILogger<OperatorAuthorizationFilter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var endpointRequiresOperator =
            context.ActionDescriptor.EndpointMetadata.OfType<RequiresOperatorAttribute>().Any();

        if (!endpointRequiresOperator || !_options.RequireOperatorKey)
        {
            await next();
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.OperatorKey))
        {
            // Fail closed for anyone but the local machine. Real remote/server deployments always
            // configure a key (docker-compose.cloud.yml refuses to start without one), so an
            // unconfigured key only happens on a fresh local machine - a bare `dotnet run`, or the
            // packaged desktop product before its first launch has set one. Trust same-machine
            // requests only in that case, so first-run local use isn't blocked on a manual config
            // edit; any request that didn't originate from this machine still gets refused below.
            var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
            if (remoteIp is not null && IPAddress.IsLoopback(remoteIp))
            {
                _logger.LogWarning(
                    "SreSecurity:OperatorKey is not configured; allowing {Path} because the request originated from this machine.",
                    context.HttpContext.Request.Path);
                await next();
                return;
            }

            _logger.LogError("SreSecurity:RequireOperatorKey is enabled but no OperatorKey is configured");
            context.Result = Deny("Operator authorization is misconfigured on the server.", "AUTH_MISCONFIGURED");
            return;
        }

        var provided = context.HttpContext.Request.Headers[_options.HeaderName].ToString();

        // Fixed-time comparison: the key is a secret, and a length-or-prefix leak is still a leak.
        if (string.IsNullOrEmpty(provided) || !FixedTimeEquals(provided, _options.OperatorKey))
        {
            _logger.LogWarning("Rejected unauthorized operator action on {Path}", context.HttpContext.Request.Path);
            context.Result = Deny("A valid operator key is required for this action.", "OPERATOR_KEY_REQUIRED");
            return;
        }

        await next();
    }

    private static ObjectResult Deny(string message, string code) =>
        new(new ApiResponse<object>
        {
            Success = false,
            Error = message,
            ErrorCode = code,
            StatusCode = StatusCodes.Status401Unauthorized
        })
        {
            StatusCode = StatusCodes.Status401Unauthorized
        };

    private static bool FixedTimeEquals(string a, string b)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(a);
        var right = System.Text.Encoding.UTF8.GetBytes(b);
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
