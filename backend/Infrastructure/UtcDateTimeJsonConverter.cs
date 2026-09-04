using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kairon.Backend.Infrastructure;

/// <summary>
/// SQLite materializes DateTime values with Kind=Unspecified. KAIRON stores those values as UTC,
/// so serializing them without a timezone suffix makes browsers incorrectly treat UTC as local
/// time. This converter restores the storage contract at the API boundary and always emits UTC.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Normalize(reader.GetDateTime());

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(Normalize(value));

    internal static DateTime Normalize(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
