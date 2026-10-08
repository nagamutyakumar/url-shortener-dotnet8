using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace UrlShortener.Api.Shortening;

/// <summary>
/// Reads an ISO 8601 timestamp that must end in "Z" or a "+hh:mm"/"-hh:mm" offset.
/// </summary>
/// <remarks>
/// The built-in <see cref="DateTimeOffset"/> converter accepts offset-less values and
/// silently applies the server's local offset, so the suffix is checked here. Failures
/// throw <see cref="JsonException"/>, which minimal APIs turn into a 400 response before
/// the handler runs. Fractional seconds are kept as parsed (up to 100 ns); truncation to
/// the stored precision happens in <see cref="UrlShorteningService"/>.
/// </remarks>
public sealed partial class ExplicitOffsetDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.String
            || !reader.TryGetDateTimeOffset(out var value)
            || !ExplicitOffsetSuffix().IsMatch(reader.GetString()!))
        {
            throw new JsonException(
                "Expected an ISO 8601 timestamp ending in 'Z' or an offset such as '+02:00'.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteStringValue(v);
        }
        else
        {
            writer.WriteNullValue();
        }
    }

    // Only checked after a successful ISO 8601 parse, where an offset-less value ends in a
    // time ("T12:00") or date ("-10-08") and so cannot match.
    [GeneratedRegex(@"(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitOffsetSuffix();
}
