// File: src/OnlineStoreSystem.Shared/Json/UtcDateTimeJsonConverter.cs
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OnlineStoreSystem.Shared.Json;

/// <summary>
/// SQL Server <c>datetime2</c> values come back from EF Core with <see cref="DateTimeKind.Unspecified"/>.
/// All timestamps in this system are stored in UTC (columns are named *Utc), so this converter writes
/// them as ISO 8601 with a trailing <c>Z</c> and normalises inbound values to UTC.
/// </summary>
public sealed class UtcDateTimeJsonConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetDateTime();
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
    {
        var utc = value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

        writer.WriteStringValue(utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK", CultureInfo.InvariantCulture));
    }
}
