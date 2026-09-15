using System.IO;
using Xunit;

namespace Kairon.Backend.Tests;

/// <summary>
/// RB-009: installer/Kairon.iss is Inno Setup Pascal Script - there is no compiler available in
/// this environment (ISCC.exe is not installed here) to actually build and exercise it, so this
/// is a best-effort text-content safety net, not a substitute for a real compile. It exists to
/// catch an obvious regression (e.g. someone reverting InitializeSetup back to always continuing)
/// rather than to prove the Pascal Script itself is correct.
/// </summary>
public sealed class InstallerScriptContentTests
{
    private static string ScriptContent()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "installer", "Kairon.iss");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate installer/Kairon.iss");
    }

    [Fact]
    public void MissingWebView2CanAbortSetupRatherThanAlwaysContinuing()
    {
        var content = ScriptContent();

        // The old bug: InitializeSetup unconditionally set Result := True before ever checking
        // IsWebView2RuntimeInstalled, so a missing runtime could never abort Setup. The fix makes
        // the negative branch's Result depend on an actual operator choice (MsgBox's return
        // value), not a value already fixed beforehand.
        Assert.Contains("function InitializeSetup: Boolean;", content);
        Assert.Contains("IsWebView2RuntimeInstalled", content);
        Assert.Contains("MB_OKCANCEL", content);
        Assert.Contains("IDOK = MsgBox(", content);
    }

    [Fact]
    public void AnAlreadyInstalledWebView2RuntimeStillContinuesNormally()
    {
        var content = ScriptContent();

        // The positive branch must remain a plain, unconditional continue - preserving normal
        // installation behavior when the prerequisite is already present.
        Assert.Contains("if IsWebView2RuntimeInstalled then", content);
    }
}
