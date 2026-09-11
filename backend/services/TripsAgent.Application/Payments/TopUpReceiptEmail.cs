using System.Net;
using TripsAgent.Application.Notifications;
using TripsAgent.Domain.Common;

namespace TripsAgent.Application.Payments;

/// <summary>The receipt an agent gets after topping up.</summary>
public static class TopUpReceiptEmail
{
    public static EmailMessage Create(string to, string firstName, Money amount, string currency, string reference)
    {
        var name = WebUtility.HtmlEncode(firstName);
        var formatted = $"{currency} {amount}";

        return new EmailMessage(
            to,
            $"Your wallet has been topped up — {formatted}",
            $"""
             <!doctype html>
             <html lang="en">
               <body>
                 <p>Hello {name},</p>
                 <p>We've added <strong>{formatted}</strong> to your wallet.</p>
                 <p>Reference: {WebUtility.HtmlEncode(reference)}</p>
                 <p>You can see the full statement in your console.</p>
               </body>
             </html>
             """,
            $"""
             Hello {firstName},

             We've added {formatted} to your wallet.

             Reference: {reference}

             You can see the full statement in your console.
             """);
    }
}
