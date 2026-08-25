using AIDIP.Backend.Configuration;
using AIDIP.Backend.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AIDIP.Backend.Infrastructure;

/// <summary>
/// Marks an endpoint that changes remediation state - approve, reject, cancel, execute. When
/// SreSecurity:RequireOperatorKey is on, these require a server-side key (PRD section 19).
///
/// Off by default so the hackathon demo runs with no setup, but the enforcement path is real
/// rather than a stub, so turning it on is a configuration change and not a code change.
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
            // Fail closed. A protected endpoint with no key configured must refuse, not wave
            // everyone through.
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
