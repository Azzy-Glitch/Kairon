using System.Text.RegularExpressions;

namespace AIDIP.Backend.Services.Audit;

/// <summary>
/// Secret-safe logging (PRD section 19). Anything that reaches a log line, an audit row, or an
/// operator-facing error message passes through here first. It is deliberately blunt: over-
/// redacting a message is cheap, leaking an API key is not.
/// </summary>
public static partial class Redaction
{
    private const string Mask = "[redacted]";

    [GeneratedRegex(@"(?i)\b(api[_-]?key|apikey|authorization|bearer|token|secret|password|pwd|connectionstring)\b\s*[:=]\s*[""']?([^\s""',;]+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9\-._~+/]+=*", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    // Provider key shapes that would otherwise sail through the key/value pattern when they
    // appear bare in an exception message.
    [GeneratedRegex(@"\bsk-[A-Za-z0-9]{16,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex OpenAiStylePattern();

    [GeneratedRegex(@"\bAIza[0-9A-Za-z\-_]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleStylePattern();

    [GeneratedRegex(@"(?i)(Password|Pwd|User\s*ID|Uid)\s*=\s*[^;]+", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectionStringPattern();

    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Bearer first: "Authorization: Bearer <token>" would otherwise match the key/value
        // pattern, which consumes only the word "Bearer" and leaves the token exposed.
        var scrubbed = BearerPattern().Replace(value, $"Bearer {Mask}");
        scrubbed = KeyValuePattern().Replace(scrubbed, m => $"{m.Groups[1].Value}={Mask}");
        scrubbed = OpenAiStylePattern().Replace(scrubbed, Mask);
        scrubbed = GoogleStylePattern().Replace(scrubbed, Mask);
        scrubbed = ConnectionStringPattern().Replace(scrubbed, m => $"{m.Groups[1].Value}={Mask}");
        return scrubbed;
    }

    /// <summary>
    /// Operator-facing description of an exception. Never includes the stack trace, because the
    /// frontend PRD (section 18) requires that stack traces never reach the UI.
    /// </summary>
    public static string Describe(Exception ex) =>
        Scrub($"{ex.GetType().Name}: {ex.Message}") ?? ex.GetType().Name;
}
