namespace Kairon.Backend.Configuration;

/// <summary>Where the demo-environment remediation tools point (PRD section 20).</summary>
public class DemoEnvironmentOptions
{
    public const string SectionName = "DemoEnvironment";

    public string BaseUrl { get; set; } = "http://localhost:5080";
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// When the demo app is not running, tools fall back to an in-process simulator so the whole
    /// lifecycle still demonstrates end to end. Disable to make tool failures real.
    /// </summary>
    public bool AllowLocalSimulatorFallback { get; set; } = true;
}
