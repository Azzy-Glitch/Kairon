using System.Text.RegularExpressions;

namespace Kairon.SDK;

/// <summary>
/// Masks common credential forms in exception text and endpoints before telemetry enters the
/// in-memory queue - the same three patterns, in the same order, as the Python SDK's _scrub_text,
/// so both SDKs send equally safe error data. The backend scrubs again on receipt; this is the
/// first line of defence, not the only one.
/// </summary>
internal static class KaironScrubber
{
    private static readonly Regex Bearer = new(@"(?i)bearer\s+[A-Za-z0-9._~+/-]+=*", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KeyValue = new(
        @"(?i)\b(api[_-]?key|authorization|token|secret|password|pwd|pairing[_-]?code)\b\s*[:=]\s*[^\s,;'""]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex KaironCredential = new(@"(?i)\b(?:krn|ksi|pair)_[A-Za-z0-9_-]{8,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        value = Bearer.Replace(value, "Bearer [redacted]");
        value = KeyValue.Replace(value, match => match.Groups[1].Value + "=[redacted]");
        return KaironCredential.Replace(value, "[redacted]");
    }
}
