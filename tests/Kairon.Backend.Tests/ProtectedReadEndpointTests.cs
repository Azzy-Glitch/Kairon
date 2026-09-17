using Kairon.Backend.Controllers;
using Kairon.Backend.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class ProtectedReadEndpointTests
{
    [Theory]
    [InlineData(typeof(AgentController), nameof(AgentController.Machines))]
    [InlineData(typeof(AgentController), nameof(AgentController.Applications))]
    // AgentController.Register is deliberately absent: enrollment is its own boundary, asserted in
    // AgentEnrollmentAuthorizationTests, and must not be gated by (or grant) the operator key.
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.GetIncidents))]
    [InlineData(typeof(TelemetryController), nameof(TelemetryController.GetMetrics))]
    [InlineData(typeof(HealthStatusController), nameof(HealthStatusController.Status))]
    [InlineData(typeof(AiConfigController), nameof(AiConfigController.Get))]
    [InlineData(typeof(AiConfigController), nameof(AiConfigController.Save))]
    [InlineData(typeof(AiConfigController), nameof(AiConfigController.Test))]
    [InlineData(typeof(AiConfigController), nameof(AiConfigController.Models))]
    public void SensitiveReadEndpointsRequireOperatorAuthorization(Type controller, string methodName)
    {
        var method = controller.GetMethod(methodName);

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttributes(typeof(RequiresOperatorAttribute), inherit: true).SingleOrDefault());
    }

    [Theory]
    [InlineData(nameof(TelemetryController.CreateIncident))]
    [InlineData(nameof(TelemetryController.CreateMetric))]
    [InlineData(nameof(TelemetryController.CreateEvent))]
    public void LegacyTelemetryIngestionUsesBoundedRatePolicy(string methodName)
    {
        var method = typeof(TelemetryController).GetMethod(methodName);
        var policy = Assert.Single(method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>());

        Assert.Equal("telemetry", policy.PolicyName);
    }

    // A cheap, fast companion to the live PairingRateLimitTests (which prove the policy actually
    // throttles over a real HTTP pipeline): this just guards against the attribute silently
    // disappearing from the wrong method in a future edit.
    [Theory]
    [InlineData(nameof(SdkPairingController.Pair))]
    [InlineData(nameof(SdkPairingController.Confirm))]
    public void UnattendedPairingEndpointsUseTheDedicatedPairingRatePolicy(string methodName)
    {
        var method = typeof(SdkPairingController).GetMethod(methodName);
        var policy = Assert.Single(method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true)
            .Cast<EnableRateLimitingAttribute>());

        Assert.Equal("pairing", policy.PolicyName);
    }

    // The operator-gated pairing endpoints are reached only by someone who already holds the
    // operator key - they are not the unattended, no-credential surface the "pairing" policy exists
    // to protect, and must not be silently swept into it (or any other policy) by a future edit.
    [Theory]
    [InlineData(nameof(SdkPairingController.Create))]
    [InlineData(nameof(SdkPairingController.RevokePairing))]
    [InlineData(nameof(SdkPairingController.GetStatus))]
    [InlineData(nameof(SdkPairingController.CompleteRepair))]
    public void OperatorGatedPairingEndpointsCarryNoRateLimitPolicy(string methodName)
    {
        var method = typeof(SdkPairingController).GetMethod(methodName);

        Assert.Empty(method!.GetCustomAttributes(typeof(EnableRateLimitingAttribute), inherit: true));
    }
}
