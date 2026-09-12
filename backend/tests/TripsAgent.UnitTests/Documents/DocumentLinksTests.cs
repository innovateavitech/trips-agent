using System.Security.Cryptography;
using FluentAssertions;
using TripsAgent.Application.Documents;
using TripsAgent.Infrastructure.Identity;

namespace TripsAgent.UnitTests.Documents;

/// <summary>
/// The links a PDF is downloaded through (#46). Each is the whole credential — a new tab carries no
/// token — so each has to be impossible to bend to another document or another purpose.
/// </summary>
public class DocumentLinksTests
{
    private static readonly Guid Document = Guid.CreateVersion7();

    private readonly MovableClock _clock = new(new DateTimeOffset(2026, 9, 11, 10, 0, 0, TimeSpan.Zero));
    private readonly DocumentLinks _links;

    public DocumentLinksTests() =>
        _links = new DocumentLinks(new HmacTokenHasher(RandomNumberGenerator.GetBytes(32)), _clock);

    [Fact]
    public void An_agents_link_works_until_it_expires()
    {
        var (expires, signature) = Parse(_links.DownloadFor(Document).Path);

        _links.IsValidDownload(Document, expires, signature).Should().BeTrue();

        _clock.Now += DocumentLinks.DownloadLifetime + TimeSpan.FromSeconds(1);

        _links.IsValidDownload(Document, expires, signature).Should().BeFalse();
    }

    [Fact]
    public void An_agents_link_opens_its_own_document_and_nothing_else()
    {
        var (expires, signature) = Parse(_links.DownloadFor(Document).Path);

        _links.IsValidDownload(Guid.CreateVersion7(), expires, signature).Should().BeFalse();
        _links.IsValidDownload(Document, expires + 3_600, signature).Should().BeFalse("moving the deadline breaks the signature");
        _links.IsValidDownload(Document, expires, signature[..^2] + "AA").Should().BeFalse();
        _links.IsValidDownload(Document, expires, null).Should().BeFalse();
    }

    [Fact]
    public void A_customers_link_never_expires_and_cannot_pass_as_an_agents()
    {
        var token = _links.PublicPathFor(Document).Split('/')[^1];

        _clock.Now += TimeSpan.FromDays(3_650);

        _links.IsValidPublic(Document, token).Should().BeTrue();
        _links.IsValidPublic(Guid.CreateVersion7(), token).Should().BeFalse();
        _links.IsValidDownload(Document, _clock.Now.AddHours(1).ToUnixTimeSeconds(), token).Should().BeFalse();
    }

    [Fact]
    public void Every_signature_is_safe_in_a_url_without_escaping()
    {
        var (_, signature) = Parse(_links.DownloadFor(Document).Path);
        var token = _links.PublicPathFor(Document).Split('/')[^1];

        signature.Should().MatchRegex("^[A-Za-z0-9_-]+$");
        token.Should().MatchRegex("^[A-Za-z0-9_-]+$");
    }

    private static (long Expires, string Signature) Parse(string path)
    {
        var query = path[(path.IndexOf('?', StringComparison.Ordinal) + 1)..]
            .Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);

        return (long.Parse(query["expires"], System.Globalization.CultureInfo.InvariantCulture), query["signature"]);
    }

    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
