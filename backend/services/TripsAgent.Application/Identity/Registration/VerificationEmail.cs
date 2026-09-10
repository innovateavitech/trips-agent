using System.Net;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>Renders the email that carries a verification code.</summary>
/// <remarks>
/// <para>
/// This one is correctly branded as Trips Agent. CLAUDE.md rule 4 keeps our brand off anything a
/// <i>traveller</i> sees; the recipient here is a travel business signing up with us, so it is our
/// own customer relationship. Traveller-facing email will render from the agency's branding record.
/// </para>
/// <para>
/// No colours, no images, no web fonts: it has to read correctly in every mail client, with images
/// blocked, and to a screen reader. The code is the only thing that matters, so it gets the
/// emphasis.
/// </para>
/// </remarks>
public static class VerificationEmail
{
    public const string ProductName = "Trips Agent";

    public static EmailMessage Create(string to, string firstName, string code, TimeSpan validFor)
    {
        // Anything the user typed is HTML-encoded before it goes into markup. A first name of
        // "<a href=…>" must arrive as text, not as a working link in an email that looks like ours.
        var name = WebUtility.HtmlEncode(firstName);
        var minutes = (int)validFor.TotalMinutes;

        var subject = $"{code} is your {ProductName} verification code";

        var html = $"""
            <!doctype html>
            <html lang="en">
              <body>
                <p>Hello {name},</p>
                <p>Use this code to verify your email address and finish setting up your
                   {ProductName} account:</p>
                <p style="font-size:28px;font-weight:bold;letter-spacing:4px">{code}</p>
                <p>The code expires in {minutes} minutes and can be used once.</p>
                <p>If you did not try to create an account, you can ignore this email — nothing
                   will happen without the code.</p>
              </body>
            </html>
            """;

        var text = $"""
            Hello {firstName},

            Use this code to verify your email address and finish setting up your {ProductName} account:

                {code}

            The code expires in {minutes} minutes and can be used once.

            If you did not try to create an account, you can ignore this email — nothing will happen without the code.
            """;

        return new EmailMessage(to, subject, html, text);
    }
}
