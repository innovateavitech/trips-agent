using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TripsAgent.Domain.Suppliers;

/// <summary>
/// Removes credentials and passenger document numbers from a supplier call before it is stored.
/// </summary>
/// <remarks>
/// <para>
/// <c>supplier_api_calls</c> keeps every request and response for months. A booking request carries
/// each passenger's passport number (plan §2.7: DOCS/DOCO records), and a supplier may echo a key
/// or token back in a response. <see cref="SupplierApiCall.Record"/> runs every body through here,
/// so no caller can store one unredacted by forgetting to ask.
/// </para>
/// <para>
/// <b>Four layers, each catching what the one before cannot.</b>
/// </para>
/// <list type="number">
/// <item><b>JSON, by property name.</b> Objects and arrays are walked, and the value of any property
/// whose name is sensitive is replaced — whatever its type, so a passport sent as an object or a
/// number goes too. Names are compared case-insensitively with <c>_</c>, <c>-</c> and spaces
/// removed, so <c>DocNumber</c>, <c>doc_number</c> and <c>DOC-NUMBER</c> are one name.</item>
/// <item><b>Inside a document, the number.</b> A supplier may nest the document as
/// <c>{"Passport": {"Number": "A012…"}}</c>, where the leaf's own name says nothing. Inside any object
/// named for a document, generic identifier names (<c>Number</c>, <c>No</c>, <c>Value</c>, <c>Id</c>,
/// <c>FreeText</c>) are redacted too. An object whose values mark it as a <c>DOCS</c> or <c>DOCO</c>
/// record — an SSR's <c>{"Code": "DOCS", "FreeText": "P/NGA/A012…"}</c> — counts as a document
/// whatever it is called. And a property named for a document that holds a single value rather than
/// an object — <c>"Docs": "P/NGA/A012…"</c> — is the document itself, so its value goes, unless the
/// name says it is only the document's type.</item>
/// <item><b>A DOCS record, by shape.</b> The slash-delimited record the airline systems use
/// (<c>P/NGA/A01234567/NGA/12JUL80/M/…</c>) is recognised by its shape wherever it appears — in any
/// string value, and in a response that is not JSON — so it is caught whatever field it travels in.</item>
/// <item><b>Known secrets, by value.</b> Every value of a sensitive request header — the merchant key,
/// the bearer token — is replaced wherever it appears in either body, whatever it is called there
/// and whether or not the body is JSON. This is what catches an echo in an HTML error page.</item>
/// </list>
/// <para>
/// <b>Duplicate property names are kept.</b> JSON allows them and a supplier's serializer may emit
/// them. The body is read with <see cref="JsonDocument"/>, which keeps both, rather than a
/// dictionary-backed <c>JsonNode</c>, which throws — and a throw here would lose the whole call
/// record, request and response, when it is most likely to be needed.
/// </para>
/// <para>
/// <b>A body that is not JSON.</b> We write every request as JSON, so a request body that does not
/// parse is either a bug or a format whose fields we cannot reason about — and requests are where
/// passport numbers live. It is withheld, keeping only its length. A response that does not parse is
/// usually a supplier in trouble returning an HTML error page, and that page is the evidence of the
/// outage, so it is kept after the known-secret pass.
/// </para>
/// <para>
/// <b>This is a deny list</b>, like the audit log's (<c>AuditRedactionPolicy</c>): a new sensitive
/// field with an unpredictable name must be added here in the same pull request. Names are matched
/// by fragment so the unpredicted ones are still mostly caught; over-redacting costs a field of
/// evidence, under-redacting costs a passport number.
/// </para>
/// </remarks>
public static partial class SupplierPayloadRedaction
{
    /// <summary>What a sensitive value is replaced with.</summary>
    public const string RedactedValue = "[redacted]";

    /// <summary>
    /// Shorter than this, a header value is not scrubbed from bodies by value: replacing every
    /// "ACCESS" or "json" in a response would destroy the evidence to protect nothing.
    /// </summary>
    public const int MinimumKnownSecretLength = 8;

    /// <summary>
    /// Name fragments that mark a sensitive property. Substring match on the normalised name.
    /// </summary>
    /// <remarks>
    /// Deliberately not a bare <c>key</c>: Trips Africa's offers and fares are identified by keys,
    /// and a dispute about which fare was booked needs them. Keys that are credentials are named
    /// for what they unlock (<c>MerchantKey</c>, <c>ApiKey</c>) and are listed that way.
    /// </remarks>
    private static readonly string[] SensitiveFragments =
    [
        // Travel documents.
        "passport", "docnumber", "documentnumber", "docno", "documentno", "visanumber", "nationalid",
        "idnumber", "identificationnumber", "documentid", "docid",

        // Credentials.
        "merchantkey", "apikey", "secretkey", "privatekey", "accesskey", "token", "secret",
        "password", "passphrase", "authorization", "signature", "hash",

        // Payment cards.
        "cardnumber", "cvv", "cvc", "securitycode",
    ];

    /// <summary>
    /// Sensitive names too short to match as fragments: <c>pin</c> would take <c>StoppingPoints</c>
    /// with it, and <c>pan</c> would take <c>Company</c>. Compared whole.
    /// </summary>
    private static readonly HashSet<string> SensitiveExactNames = ["pin", "nin", "pan", "bvn", "otp", "cvv2"];

    /// <summary>Name fragments that make an object a travel document, for the second layer.</summary>
    private static readonly string[] DocumentContainerFragments = ["document", "passport", "visa", "docs", "doco"];

    /// <summary>Generic names that, inside a document, are the document's number.</summary>
    private static readonly HashSet<string> DocumentNumberNames = ["number", "no", "num", "value", "id", "freetext", "text"];

    /// <summary>
    /// Values that mark the object holding them as a travel document record: the SSR codes for
    /// passport and visa data (plan §2.7).
    /// </summary>
    private static readonly HashSet<string> DocumentRecordCodes = new(StringComparer.OrdinalIgnoreCase) { "DOCS", "DOCO" };

    /// <summary>
    /// Relaxed escaping so a redacted body reads like the original: the default encoder would turn
    /// every <c>+</c> in a phone number into <c>\u002B</c>. This is stored text, never HTML.
    /// </summary>
    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>True when a JSON property of this name must never be stored.</summary>
    public static bool IsSensitiveProperty(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var name = Normalise(propertyName);

        return SensitiveExactNames.Contains(name)
               || Array.Exists(SensitiveFragments, fragment => name.Contains(fragment, StringComparison.Ordinal));
    }

    /// <summary>Redacts a request body. One that is not JSON is withheld; see the remarks.</summary>
    public static string? RedactRequestBody(string? body, IEnumerable<string> knownSecrets)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return TryRedactJson(body, out var redacted)
            ? ScrubKnownSecrets(redacted, knownSecrets)
            : $"[withheld: the request body was not JSON, so its fields could not be redacted ({body.Length} characters)]";
    }

    /// <summary>Redacts a response body. One that is not JSON is kept after the known-secret pass.</summary>
    public static string? RedactResponseBody(string? body, IEnumerable<string> knownSecrets)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return body;
        }

        return ScrubKnownSecrets(TryRedactJson(body, out var redacted) ? redacted : ScrubDocsRecords(body), knownSecrets);
    }

    /// <summary>
    /// Redacts the values of sensitive query-string parameters: <c>?token=…</c> is as much a
    /// credential as the header it would otherwise have been.
    /// </summary>
    public static string RedactEndpoint(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var queryStart = endpoint.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return endpoint;
        }

        var parameters = endpoint[(queryStart + 1)..].Split('&');

        for (var i = 0; i < parameters.Length; i++)
        {
            var equals = parameters[i].IndexOf('=', StringComparison.Ordinal);
            var name = equals < 0 ? parameters[i] : parameters[i][..equals];

            if (equals >= 0 && IsSensitiveProperty(Uri.UnescapeDataString(name)))
            {
                parameters[i] = $"{name}={RedactedValue}";
            }
        }

        return $"{endpoint[..queryStart]}?{string.Join('&', parameters)}";
    }

    /// <summary>
    /// The values worth scrubbing by value: each sensitive header's value, and the token alone
    /// where the value is a scheme and a token (<c>Bearer abc…</c>), since a body would echo the
    /// token without its scheme.
    /// </summary>
    public static IReadOnlyList<string> KnownSecretsFrom(IReadOnlyDictionary<string, string> requestHeaders)
    {
        ArgumentNullException.ThrowIfNull(requestHeaders);

        var secrets = new List<string>();

        foreach (var (name, value) in requestHeaders)
        {
            if (!SupplierApiCall.IsSensitiveHeader(name) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            secrets.Add(value);

            var space = value.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0)
            {
                secrets.Add(value[(space + 1)..].Trim());
            }
        }

        // Longest first, so a token is not half-replaced by a shorter secret it happens to contain.
        return secrets
            .Where(secret => secret.Length >= MinimumKnownSecretLength)
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(secret => secret.Length)
            .ToList();
    }

    /// <summary>Replaces every DOCS-shaped record in free text, such as an HTML error page.</summary>
    private static string ScrubDocsRecords(string text) => DocsRecordPattern().Replace(text, RedactedValue);

    private static string ScrubKnownSecrets(string text, IEnumerable<string> knownSecrets)
    {
        foreach (var secret in knownSecrets)
        {
            if (secret.Length >= MinimumKnownSecretLength)
            {
                text = text.Replace(secret, RedactedValue, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>
    /// Parses and redacts. When nothing needed redacting the original text is kept exactly, so
    /// the evidence is byte-for-byte what was sent or received.
    /// </summary>
    /// <remarks>
    /// False when the body cannot be read as JSON at all, so the caller applies the non-JSON policy.
    /// Parsing is the only step expected to fail, but whatever fails, the caller gets that policy
    /// rather than an exception: an exception here loses the whole call record.
    /// </remarks>
    private static bool TryRedactJson(string body, out string redacted)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            using var output = new MemoryStream(body.Length);
            bool changed;

            using (var writer = new Utf8JsonWriter(output, WriteOptions))
            {
                changed = WriteRedacted(document.RootElement, writer, insideDocument: false);
            }

            redacted = changed ? Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length) : body;
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            redacted = body;
            return false;
        }
    }

    /// <summary>
    /// Copies one element to <paramref name="writer"/>, replacing sensitive values on the way. True
    /// when anything was replaced.
    /// </summary>
    private static bool WriteRedacted(JsonElement element, Utf8JsonWriter writer, bool insideDocument)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                return WriteRedactedObject(element, writer, insideDocument || IsDocumentRecord(element));

            case JsonValueKind.Array:
                var changed = false;
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                {
                    changed |= WriteRedacted(item, writer, insideDocument);
                }

                writer.WriteEndArray();
                return changed;

            case JsonValueKind.String when DocsRecordPattern().IsMatch(element.GetString()!):
                writer.WriteStringValue(RedactedValue);
                return true;

            default:
                element.WriteTo(writer);
                return false;
        }
    }

    private static bool WriteRedactedObject(JsonElement element, Utf8JsonWriter writer, bool insideDocument)
    {
        var changed = false;
        writer.WriteStartObject();

        // EnumerateObject yields every property, a repeated name included, in the order written.
        foreach (var property in element.EnumerateObject())
        {
            writer.WritePropertyName(property.Name);

            var name = Normalise(property.Name);
            var namedForDocument = Array.Exists(
                DocumentContainerFragments,
                fragment => name.Contains(fragment, StringComparison.Ordinal));

            var sensitive = IsSensitiveProperty(property.Name)
                            || (insideDocument && DocumentNumberNames.Contains(name))
                            || (namedForDocument && IsScalar(property.Value) && !name.EndsWith("type", StringComparison.Ordinal));

            if (sensitive)
            {
                writer.WriteStringValue(RedactedValue);
                changed = true;
                continue;
            }

            changed |= WriteRedacted(property.Value, writer, insideDocument || namedForDocument);
        }

        writer.WriteEndObject();
        return changed;
    }

    /// <summary>True when one of the object's own values is a DOCS or DOCO code.</summary>
    private static bool IsDocumentRecord(JsonElement obj)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String
                && DocumentRecordCodes.Contains(property.Value.GetString()!.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsScalar(JsonElement value) =>
        value.ValueKind is JsonValueKind.String or JsonValueKind.Number;

    /// <summary>
    /// A DOCS record: document type, issuing country, number, nationality and date of birth, then
    /// sex, expiry and name — <c>P/NGA/A01234567/NGA/12JUL80/M/20NOV28/OKAFOR/ADAEZE</c>. Matched up
    /// to the date of birth, which no URL or reference looks like, and taken to the end of the record.
    /// Every quantifier is short and bounded, so there is no catastrophic backtracking to guard against.
    /// </summary>
    [GeneratedRegex(@"\b[A-Z]{1,2}/[A-Z]{2,3}/[A-Z0-9]{3,20}/[A-Z]{2,3}/\d{2}[A-Z]{3}\d{2}[^\s""'<>]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DocsRecordPattern();

    private static string Normalise(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}
