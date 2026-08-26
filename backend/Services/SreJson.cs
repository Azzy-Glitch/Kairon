using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairon.Backend.Services;

/// <summary>
/// One serializer configuration for everything the SRE layer persists or sends. Having a single
/// place for this is what keeps the JSON columns readable and the AI wire contract stable.
/// </summary>
public static class SreJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes a persisted JSON column, returning the fallback instead of throwing. Persisted
    /// JSON is our own data, but a malformed column must never take down an incident view.
    /// </summary>
    public static T Deserialize<T>(string? json, T fallback)
    {
        if (string.IsNullOrWhiteSpace(json))
            return fallback;

        try
        {
            return JsonSerializer.Deserialize<T>(json, ReadOptions) ?? fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    /// <summary>Caps a JSON payload so a single evidence blob cannot grow without bound.</summary>
    public static string Truncate(string json, int maxChars)
    {
        if (json.Length <= maxChars)
            return json;

        return json[..maxChars] + "\"...truncated\"";
    }
}
