using System.Net;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Tenancy.Kyb;

/// <summary>The emails an agency receives when its KYB submission is decided.</summary>
/// <remarks>
/// Agent-facing, so the Trips brand is correct here — CLAUDE.md rule 4 keeps our brand off
/// anything a <i>traveller</i> sees. Everything the agency typed, and everything an admin typed
/// into a rejection reason, is HTML-encoded on the way in.
/// </remarks>
public static class KybDecisionEmail
{
    public const string ProductName = "Trips Agent";

    public static EmailMessage Approved(string to, string businessName)
    {
        var name = WebUtility.HtmlEncode(businessName);

        return new EmailMessage(
            to,
            $"{businessName} is verified",
            $"""
             <!doctype html>
             <html lang="en">
               <body>
                 <p>Good news — {name} is verified.</p>
                 <p>You can now add funds to your wallet and start booking.</p>
                 <p>Sign in to your {ProductName} console to get started.</p>
               </body>
             </html>
             """,
            $"""
             Good news — {businessName} is verified.

             You can now add funds to your wallet and start booking.

             Sign in to your {ProductName} console to get started.
             """);
    }

    public static EmailMessage Rejected(string to, string businessName, string reason)
    {
        var name = WebUtility.HtmlEncode(businessName);

        // An admin typed this. It goes into an email, so it is encoded like any other input.
        var encodedReason = WebUtility.HtmlEncode(reason);

        return new EmailMessage(
            to,
            $"We need something else for {businessName}",
            $"""
             <!doctype html>
             <html lang="en">
               <body>
                 <p>We could not verify {name} yet.</p>
                 <p><strong>What we need:</strong></p>
                 <blockquote>{encodedReason}</blockquote>
                 <p>Sign in to your {ProductName} console, upload the corrected documents and
                    submit again. There is no limit on how many times you can try.</p>
               </body>
             </html>
             """,
            $"""
             We could not verify {businessName} yet.

             What we need:

                 {reason}

             Sign in to your {ProductName} console, upload the corrected documents and submit
             again. There is no limit on how many times you can try.
             """);
    }
}
