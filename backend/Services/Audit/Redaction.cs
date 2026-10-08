using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Kairon.Backend.Services.Audit;

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

    [GeneratedRegex(@"(?i)\b(?:AKIA|ASIA)[A-Z0-9]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex AdditionalTokenPattern();

    [GeneratedRegex(@"(?i)\b(?:krn|ksi|pair)_[A-Za-z0-9_-]{8,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex KaironCredentialPattern();

    // Compound names the word-bounded KeyValuePattern misses because "_"/"-" are word characters:
    // access_token=, refresh-token:, X-Session-Id=, client_secret=, api_signature=, sessionid= ...
    // Query strings and exception text routinely carry these.
    [GeneratedRegex(@"(?i)\b([A-Za-z0-9_.-]*(?:token|secret|passw(?:or)?d|pwd|api[_-]?key|session[_-]?id|sessionid|signature|credential)[A-Za-z0-9_.-]*)\s*[:=]\s*[""']?[^\s""',;&]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex CompoundSecretPattern();

    // Whole cookie headers: a cookie value is a session credential whatever its name.
    [GeneratedRegex(@"(?i)\b(set-cookie|cookie)\s*:\s*[^\r\n]+", RegexOptions.CultureInvariant)]
    private static partial Regex CookieHeaderPattern();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\Users\\[^\\\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex UserProfilePathPattern();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.CultureInvariant)]
    private static partial Regex IpAddressPattern();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    // Field names whose string value is a secret whatever it looks like: a structured payload
    // ({"accessToken":"..."}) carries no "=" or ":" next to the value for the text patterns to see.
    [GeneratedRegex(@"(?i)token|secret|passw(?:or)?d|passphrase|pwd|api[_-]?key|private[_-]?key|authorization|bearer|cookie|session[_-]?id|signature|credential|connection[_-]?string",
        RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveNamePattern();

    public static bool IsSensitiveName(string? name) =>
        !string.IsNullOrEmpty(name) && SensitiveNamePattern().IsMatch(name);

    /// <summary>
    /// Scrubs a value that arrives with a field name (a JSON property, a structured log property).
    /// Under a sensitive name the whole value is masked - except a GUID, which is an identifier
    /// (a credential id, not the credential). Every other value gets the ordinary <see cref="Scrub"/>.
    /// </summary>
    public static string? ScrubNamed(string? name, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return IsSensitiveName(name) && !Guid.TryParse(value, out _) ? Mask : Scrub(value);
    }

    /// <summary>
    /// Scrubs a JSON document value by value, so the result stays valid, structured JSON: every
    /// string goes through <see cref="ScrubNamed"/> with its property name, and property names
    /// themselves through <see cref="Scrub"/>. Scrubbing the serialized text as one string would
    /// miss "name":"value" pairs (the quote defeats the key=value patterns) and could let a
    /// greedy pattern swallow the rest of the document. Unparseable input falls back to
    /// <see cref="Scrub"/> on the raw text.
    /// </summary>
    public static string? ScrubJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return Scrub(json);
        }

        if (root is null)
            return json;

        return ScrubNode(root, name: null)?.ToJsonString() ?? "null";
    }

    private static JsonNode? ScrubNode(JsonNode? node, string? name)
    {
        switch (node)
        {
            case JsonObject obj:
                var scrubbed = new JsonObject();
                foreach (var (key, child) in obj.ToList())
                {
                    obj.Remove(key);
                    var safeKey = Scrub(key) ?? key;
                    // Two keys scrubbed to the same text must not collide.
                    while (scrubbed.ContainsKey(safeKey)) safeKey += "_";
                    scrubbed[safeKey] = ScrubNode(child, key);
                }
                return scrubbed;
            case JsonArray array:
                var items = array.ToList();
                array.Clear();
                var scrubbedArray = new JsonArray();
                foreach (var item in items)
                    scrubbedArray.Add(ScrubNode(item, name));
                return scrubbedArray;
            case JsonValue value when value.TryGetValue<string>(out var text):
                return JsonValue.Create(ScrubNamed(name, text));
            default:
                return node;
        }
    }

    public static string? Scrub(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        // Bearer first: "Authorization: Bearer <token>" would otherwise match the key/value
        // pattern, which consumes only the word "Bearer" and leaves the token exposed.
        var scrubbed = BearerPattern().Replace(value, $"Bearer {Mask}");
        scrubbed = CookieHeaderPattern().Replace(scrubbed, m => $"{m.Groups[1].Value}: {Mask}");
        scrubbed = KeyValuePattern().Replace(scrubbed, m => $"{m.Groups[1].Value}={Mask}");
        scrubbed = CompoundSecretPattern().Replace(scrubbed, m => $"{m.Groups[1].Value}={Mask}");
        scrubbed = OpenAiStylePattern().Replace(scrubbed, Mask);
        scrubbed = GoogleStylePattern().Replace(scrubbed, Mask);
        scrubbed = ConnectionStringPattern().Replace(scrubbed, m => $"{m.Groups[1].Value}={Mask}");
        scrubbed = AdditionalTokenPattern().Replace(scrubbed, Mask);
        scrubbed = KaironCredentialPattern().Replace(scrubbed, Mask);
        scrubbed = UserProfilePathPattern().Replace(scrubbed, @"C:\Users\[redacted-user]");
        scrubbed = IpAddressPattern().Replace(scrubbed, "[redacted-ip]");
        scrubbed = EmailPattern().Replace(scrubbed, Mask);
        return scrubbed;
    }

    /// <summary>
    /// Operator-facing description of an exception. Never includes the stack trace, because the
    /// frontend PRD (section 18) requires that stack traces never reach the UI.
    /// </summary>
    public static string Describe(Exception ex) =>
        Scrub($"{ex.GetType().Name}: {ex.Message}") ?? ex.GetType().Name;
}
