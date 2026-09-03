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
}
