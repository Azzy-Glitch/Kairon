using Kairon.Backend.Services.Remediation;

namespace Kairon.Backend.Tests;

/// <summary>Deterministic stand-in for the local SCM inspector and the local-host boundary. By
/// default every host is "this machine" and every service is an existing, eligible application
/// service the backend may query/start/stop, with a fixed identity - so execution tests exercise
/// KAIRON's own gates and the fake SCM, never the developer machine's real services. Individual
/// tests override a single service or host to prove each refusal.</summary>
public sealed class FakeWindowsServices : IWindowsServiceInspector, ILocalMachine
{
    public const string DefaultIdentity = "0000000000000000000000000000000000000000000000000000000000000ABC";

    public static readonly FakeWindowsServices Default = new();

    public bool IsSupported { get; set; } = true;
    public Func<string, bool> LocalHost { get; set; } = _ => true;
    public Dictionary<string, WindowsServiceProbe> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsLocal(string hostName) => LocalHost(hostName);

    public WindowsServiceProbe Probe(string serviceName) =>
        Overrides.TryGetValue(serviceName, out var probe) ? probe : Eligible(serviceName);

    public IReadOnlyList<WindowsServiceSummary> ListServices() =>
        Overrides.Values.Select(p => new WindowsServiceSummary(p.ServiceName, p.DisplayName ?? p.ServiceName, p.State ?? 4, p.Eligibility)).ToList();

    public static WindowsServiceProbe Eligible(string serviceName, string identity = DefaultIdentity) => new()
    {
        ServiceName = serviceName, Exists = true, DisplayName = serviceName, State = 4,
        ImagePath = @"C:\Apps\" + serviceName + ".exe", StartAccount = @"NT AUTHORITY\LocalService", ServiceType = 0x10,
        Eligibility = ServiceEligibility.Eligible, CanQueryStatus = true, CanStart = true, CanStop = true, IdentityHash = identity
    };
}
