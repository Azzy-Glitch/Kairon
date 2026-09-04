using System.Text.Json;
using Kairon.Backend.Infrastructure;
using Xunit;

namespace Kairon.Backend.Tests;

public sealed class UtcDateTimeJsonConverterTests
{
    private static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new UtcDateTimeJsonConverter());
        return options;
    }

    [Fact]
    public void SqliteUnspecifiedDateTimeIsSerializedWithUtcMarker()
    {
        var storedUtc = new DateTime(2026, 9, 4, 5, 27, 33, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(storedUtc, Options());

        Assert.Equal("\"2026-09-04T05:27:33Z\"", json);
    }

    [Fact]
    public void TimestampWithoutOffsetIsReadAsUtc()
    {
        var parsed = JsonSerializer.Deserialize<DateTime>("\"2026-09-04T05:27:33\"", Options());

        Assert.Equal(DateTimeKind.Utc, parsed.Kind);
        Assert.Equal(new DateTime(2026, 9, 4, 5, 27, 33, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void NullableDateTimeUsesTheUtcConverter()
    {
        DateTime? storedUtc = new DateTime(2026, 9, 4, 5, 27, 33, DateTimeKind.Unspecified);

        var json = JsonSerializer.Serialize(storedUtc, Options());

        Assert.EndsWith("Z\"", json, StringComparison.Ordinal);
    }
}
