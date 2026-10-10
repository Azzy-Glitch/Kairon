using Kairon.Backend.DTOs.Sre;

namespace Kairon.Backend.Models.Sre;

/// <summary>
/// The AI's structured assessment of an incident beyond the diagnosis, stored as
/// <see cref="SreIncident.AssessmentJson"/>. Advisory only: nothing here is ever executed.
/// </summary>
public sealed class AiAssessment
{
    /// <summary>confirmed | likely | possible | unknown</summary>
    public string RootCauseCertainty { get; set; } = "unknown";

    public List<string> NextSteps { get; set; } = new();

    /// <summary>Each offered action the model weighed, with its verdict and reason.</summary>
    public List<ConsideredActionDto> ConsideredActions { get; set; } = new();

    /// <summary>The actions KAIRON offered the model for this incident (those with a ready,
    /// authorized target at investigation time). Empty means nothing could have been recommended.</summary>
    public List<string> OfferedActions { get; set; } = new();

    public static readonly string[] Certainties = ["confirmed", "likely", "possible", "unknown"];
}
