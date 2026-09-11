using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TripsAgent.Integrations.TripsAfrica.Wire;

/// <summary>
/// Reads one of the supplier's decimal naira amounts — <c>844519.0</c>, <c>23937.5</c> — straight into
/// kobo, exactly, as the JSON is parsed.
/// </summary>
/// <remarks>
/// <para>
/// The one place a decimal amount exists in this integration, and only as a local on its way to a
/// <see cref="long"/> (CLAUDE.md rule 2). Every wire property it reads is a <c>long?</c> named
/// <c>…Minor</c>, so past this line there is nothing to round and nothing to lose a kobo to.
/// </para>
/// <para>
/// An amount that is negative, not a whole number of kobo, too large, or not a number at all reads as
/// null — never as a rounded guess we would then charge. The mapper treats an offer with no readable
/// total as one it cannot sell, and drops it.
/// </para>
/// </remarks>
internal sealed class NairaAsKoboConverter : JsonConverter<long?>
{
    public override bool HandleNull => true;

    public override long? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Number:
                return reader.TryGetDecimal(out var number) ? ToKobo(number) : null;

            case JsonTokenType.String:
                return decimal.TryParse(reader.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var text)
                    ? ToKobo(text)
                    : null;

            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                // Not an amount at all. Consume it, so the rest of the document still reads.
                reader.Skip();
                return null;

            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, long? value, JsonSerializerOptions options) =>
        throw new NotSupportedException("Supplier amounts are only ever read, never sent.");

    /// <summary>Naira to kobo, or null when the answer would not be exact.</summary>
    internal static long? ToKobo(decimal naira)
    {
        if (naira < 0)
        {
            return null;
        }

        var scaled = naira * 100m;

        return scaled == decimal.Truncate(scaled) && scaled <= long.MaxValue ? (long)scaled : null;
    }
}
