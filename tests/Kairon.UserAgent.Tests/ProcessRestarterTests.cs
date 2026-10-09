using Kairon.UserAgent;
using Xunit;

namespace Kairon.UserAgent.Tests;

/// <summary>
/// The UserAgent restarts the command the developer actually started - through launchers of the
/// same application, never into a shell, terminal or IDE - and only after re-verifying the approved
/// process with the operating system. Paths are built natively so the rules hold on every OS.
/// </summary>
public sealed class ProcessRestarterTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "kairon-restart-tests");
    private static readonly string BasePython = Path.Combine(Root, "Python314", "python.exe");
    private static readonly string VenvPython = Path.Combine(Root, "app", ".venv", "Scripts", "python.exe");
    private static readonly string Uvicorn = Path.Combine(Root, "app", ".venv", "Scripts", "uvicorn.exe");
    private static readonly string PyLauncher = Path.Combine(Root, "Launcher", "py.exe");
    private static readonly string Shell = Path.Combine(Root, "PowerShell", "pwsh.exe");
    private static readonly DateTime T0 = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private static ProcessRestarter.ProcessFacts P(int id, string exe, int secondsAfterT0, int session = 1) =>
        new(id, session, T0.AddSeconds(secondsAfterT0), exe);

    private static ProcessRestarter.ProcessFacts Root_(ProcessRestarter.ProcessFacts start, params (ProcessRestarter.ProcessFacts Child, ProcessRestarter.ProcessFacts Parent)[] chain)
    {
        var parents = chain.ToDictionary(link => link.Child.ProcessId, link => link.Parent.ProcessId);
        var all = chain.SelectMany(link => new[] { link.Child, link.Parent }).Append(start).DistinctBy(p => p.ProcessId)
            .ToDictionary(p => p.ProcessId);
        return ProcessRestarter.ChooseRestartRoot(start, parents, id => all.GetValueOrDefault(id));
    }

    [Fact]
    public void AVirtualEnvironmentIsRestartedThroughItsOwnPythonSoItsPackagesStillLoad()
    {
        var shell = P(10, Shell, 0);
        var venv = P(20, VenvPython, 5);
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(20, Root_(interpreter, (interpreter, venv), (venv, shell)).ProcessId);
    }

    [Fact]
    public void AScriptShimLikeUvicornIsRestartedAsTheCommandTheDeveloperRan()
    {
        var shell = P(10, Shell, 0);
        var shim = P(15, Uvicorn, 4);
        var venv = P(20, VenvPython, 5);
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(15, Root_(interpreter, (interpreter, venv), (venv, shim), (shim, shell)).ProcessId);
    }

    [Fact]
    public void ThePyLauncherIsIncluded()
    {
        var launcher = P(15, PyLauncher, 4);
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(15, Root_(interpreter, (interpreter, launcher), (launcher, P(10, Shell, 0))).ProcessId);
    }

    [Fact]
    public void AShellTerminalOrIdeIsNeverRestarted()
    {
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(30, Root_(interpreter, (interpreter, P(10, Shell, 0))).ProcessId);
    }

    [Fact]
    public void ARecycledParentIdOrAnotherSessionIsNeverClimbedInto()
    {
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(30, Root_(interpreter, (interpreter, P(20, VenvPython, 60))).ProcessId);           // started later
        Assert.Equal(30, Root_(interpreter, (interpreter, P(20, VenvPython, 5, session: 2))).ProcessId); // other user
    }

    [Fact]
    public void KaironItselfIsNeverClimbedIntoOrRestarted()
    {
        var kairon = Path.Combine(Root, "Kairon", "Kairon.UserAgent.exe");
        var interpreter = P(30, BasePython, 6);
        Assert.Equal(30, Root_(interpreter, (interpreter, P(20, kairon, 1))).ProcessId);
        Assert.NotNull(ProcessRestarter.Refusal(kairon));
        Assert.Null(ProcessRestarter.Refusal(BasePython));
        Assert.NotNull(ProcessRestarter.Refusal("relative\\python.exe"));
    }

    [Fact]
    public void TheApprovedProcessIsReverifiedBeforeAnythingIsTouched()
    {
        var instruction = new ProcessRestartInstruction(Guid.NewGuid(), 30, T0.AddSeconds(6), BasePython, Root);
        Assert.Null(ProcessRestarter.Verify(P(30, BasePython, 6), instruction, ownSessionId: 1));
        Assert.Contains("different Windows session", ProcessRestarter.Verify(P(30, BasePython, 6, session: 2), instruction, 1));
        Assert.Contains("different process", ProcessRestarter.Verify(P(30, BasePython, 90), instruction, 1));
        Assert.Contains("different program", ProcessRestarter.Verify(P(30, Shell, 6), instruction, 1));
    }
}
