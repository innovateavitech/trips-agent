using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

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
/// <b>Three layers, each catching what the one before cannot.</b>
/// </para>
/// <list type="number">
/// <item><b>JSON, by property name.</b> Objects and arrays are walked, and the value of any property
/// whose name is sensitive is replaced — whatever its type, so a passport sent as an object or a
/// number goes too. Names are compared case-insensitively with <c>_</c>, <c>-</c> and spaces
/// removed, so <c>DocNumber</c>, <c>doc_number</c> and <c>DOC-NUMBER</c> are one name.</item>
/// <item><b>Inside a document, the number.</b> A supplier may nest the document as
/// <c>{"Passport": {"Number": "A012…"}}</c>, where the leaf's own name says nothing. Inside any object
/// named for a document, generic identifier names (<c>Number</c>, <c>No</c>, <c>Value</c>) are
/// redacted too.</item>
/// <item><b>Known secrets, by value.</b> Every value of a sensitive request header — the merchant key,
/// the bearer token — is replaced wherever it appears in either body, whatever it is called there
/// and whether or not the body is JSON. This is what catches an echo in an HTML error page.</item>
/// </list>
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
public static class SupplierPayloadRedaction
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
    private static readonly HashSet<string> DocumentNumberNames = ["number", "no", "num", "value"];

    /// <summary>
    /// Relaxed escaping so a redacted body reads like the original: the default encoder would turn
    /// every <c>+</c> in a phone number into <c>\u002B</c>. This is stored text, never HTML.
    /// </summary>
    private static readonly JsonSerializerOptions WriteOptions = new()
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

        return ScrubKnownSecrets(TryRedactJson(body, out var redacted) ? redacted : body, knownSecrets);
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
    private static bool TryRedactJson(string body, out string redacted)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            redacted = body;
            return false;
        }

        redacted = RedactNode(root, insideDocument: false) ? root!.ToJsonString(WriteOptions) : body;
        return true;
    }

    /// <summary>Walks one node, replacing sensitive values in place. True when anything changed.</summary>
    private static bool RedactNode(JsonNode? node, bool insideDocument)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                // Materialised: replacing a value while enumerating the object would throw.
                foreach (var (name, value) in obj.ToList())
                {
                    var normalised = Normalise(name);

                    if (IsSensitiveProperty(name) || (insideDocument && DocumentNumberNames.Contains(normalised)))
                    {
                        obj[name] = RedactedValue;
                        changed = true;
                        continue;
                    }

                    var isDocument = Array.Exists(
                        DocumentContainerFragments,
                        fragment => normalised.Contains(fragment, StringComparison.Ordinal));

                    changed |= RedactNode(value, insideDocument || isDocument);
                }

                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    changed |= RedactNode(item, insideDocument);
                }

                break;
        }

        return changed;
    }

    private static string Normalise(string name) =>
        name.Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
}
