using System.Net;
using TripsAgent.Application.Notifications;

namespace TripsAgent.Application.Identity.Registration;

/// <summary>The email carrying a password reset link.</summary>
public static class PasswordResetEmail
{
    public const string ProductName = "Trips Agent";

    public static EmailMessage Create(string to, string firstName, string resetUrl, TimeSpan validFor)
    {
        var name = WebUtility.HtmlEncode(firstName);
        var url = WebUtility.HtmlEncode(resetUrl);
        var minutes = (int)validFor.TotalMinutes;

        return new EmailMessage(
            to,
            $"Reset your {ProductName} password",
            $"""
             <!doctype html>
             <html lang="en">
               <body>
                 <p>Hello {name},</p>
                 <p>Use this link to choose a new password:</p>
                 <p><a href="{url}">Reset your password</a></p>
                 <p>The link works once and expires in {minutes} minutes.</p>
                 <p>If you did not ask for this, you can ignore this email — your password has
                    not changed, and nobody can change it without this link.</p>
               </body>
             </html>
             """,
            $"""
             Hello {firstName},

             Use this link to choose a new password:

                 {resetUrl}

             The link works once and expires in {minutes} minutes.

             If you did not ask for this, you can ignore this email — your password has not
             changed, and nobody can change it without this link.
             """);
    }
}
