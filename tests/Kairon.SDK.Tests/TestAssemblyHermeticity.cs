using System.Runtime.CompilerServices;

namespace Kairon.SDK.Tests;

/// <summary>
/// Keeps every test in this assembly away from a real Kairon.Agent. Developer machines routinely
/// run the installed Agent on its fixed loopback port (47891); any test that builds a
/// KaironTelemetryClient or KaironClient without an explicit proof port would otherwise perform a
/// genuine Agent handshake against it - nondeterministic, and it consumes real proof challenges.
/// Port 0 is never connectable, so the proof path fails fast exactly as if no Agent were installed.
/// Tests that exercise the proof deliberately still pass their own fake Agent's port explicitly.
/// </summary>
internal static class TestAssemblyHermeticity
{
    [ModuleInitializer]
    internal static void IsolateFromTheInstalledAgent() => AgentMachineProof.DefaultPort = 0;
}
