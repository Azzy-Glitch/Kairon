using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kairon.Backend.Configuration;
using Kairon.Backend.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// Marks the machine-enrollment bootstrap endpoint (POST /api/agent/register). This is a DIFFERENT
/// trust boundary from <see cref="RequiresOperatorAttribute"/> on purpose: enrolling a machine is
/// the one thing an Agent must do before it holds any credential of its own, and it must not
/// require - or grant - the operator privilege that approves and executes remediation.
///
/// An endpoint carries exactly one of these two attributes, never both.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public class RequiresAgentEnrollmentAttribute : Attribute
{
}

/// <summary>
/// Authorizes <see cref="RequiresAgentEnrollmentAttribute"/> endpoints against
/// AgentEnrollmentSecurity:EnrollmentKeys, and nothing else. Deliberately does not consult the
/// operator key: an operator key alone must never enroll a machine, and an enrollment key must
/// never reach an operator action (that direction is enforced by OperatorAuthorizationFilter,
/// which likewise only ever reads its own configured key).
/// </summary>
public class AgentEnrollmentAuthorizationFilter : IAsyncActionFilter
{
    private readonly AgentEnrollmentSecurityOptions _options;
    private readonly ILogger<AgentEnrollmentAuthorizationFilter> _logger;

    public AgentEnrollmentAuthorizationFilter(
        IOptions<AgentEnrollmentSecurityOptions> options,
        ILogger<AgentEnrollmentAuthorizationFilter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var requiresEnrollment =
            context.ActionDescriptor.EndpointMetadata.OfType<RequiresAgentEnrollmentAttribute>().Any();

        if (!requiresEnrollment || !_options.RequireEnrollmentKey)
        {
            await next();
            return;
        }

        // Blank entries are dropped first, so a config array containing "" can never be matched by
        // an absent/empty header.
        var configured = _options.EnrollmentKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToArray();

        if (configured.Length == 0)
        {
            // No enrollment key configured: the local desktop case, where the Agent and the backend
            // are the same machine and the backend's operator key is ephemeral per launch. Trust
            // same-machine registration only; anything off-box is still refused. A real remote or
            // centralized deployment always configures a key (see docker-compose.cloud.yml).
            var remoteIp = context.HttpContext.Connection.RemoteIpAddress;
            if (remoteIp is not null && IPAddress.IsLoopback(remoteIp))
            {
                await next();
                return;
            }

            _logger.LogError(
                "AgentEnrollmentSecurity:RequireEnrollmentKey is enabled but no EnrollmentKeys are configured; refusing remote registration.");
            context.Result = Deny("Machine enrollment is misconfigured on the server.", "ENROLLMENT_MISCONFIGURED");
            return;
        }

        var provided = context.HttpContext.Request.Headers[_options.HeaderName].ToString();
        if (string.IsNullOrEmpty(provided) || !MatchesAnyConfiguredKey(provided, configured))
        {
            // The rejected value is never logged - it is a secret even when it is wrong.
            _logger.LogWarning("Rejected machine enrollment with a missing or invalid enrollment key.");
            context.Result = Deny("A valid enrollment key is required to register a machine.", "ENROLLMENT_KEY_REQUIRED");
            return;
        }

        await next();
    }

    /// <summary>Every configured key is compared, and always in full, so neither the number of
    /// configured keys nor which one matched is observable through timing.</summary>
    private static bool MatchesAnyConfiguredKey(string provided, IReadOnlyList<string> configured)
    {
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var matched = false;

        foreach (var candidate in configured)
        {
            if (CryptographicOperations.FixedTimeEquals(providedBytes, Encoding.UTF8.GetBytes(candidate)))
                matched = true;
        }

        return matched;
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
}
