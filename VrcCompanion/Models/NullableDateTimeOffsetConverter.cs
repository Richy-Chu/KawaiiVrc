using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VrcCompanion.Models;

/// <summary>
/// VRChat sometimes returns an empty or non-date value for a profile's
/// last_activity/last_login fields. Treat those as absent rather than failing
/// the entire user record, which would also hide the profile image.
/// </summary>
public sealed class NullableDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        if (reader.TokenType == JsonTokenType.Number &&
            reader.TryGetInt64(out var unixTime))
        {
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(unixTime);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        reader.Skip();
        return null;
    }

    public override void Write(
        Utf8JsonWriter writer,
        DateTimeOffset? value,
        JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStringValue(value.Value);
        }
    }
}
