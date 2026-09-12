namespace TripsAgent.Domain.Crm;

// Every enum here reaches the console by name ("New", "TripRequestWidget") and is stored by name,
// so a psql query reads the word and reordering an enum can never change what a stored row means.
// They are numbered from 1, so an unset value is never mistaken for a real one.

/// <summary>Where a lead stands in the agency's pipeline.</summary>
public enum LeadStage
{
    /// <summary>Waiting for a first answer.</summary>
    New = 1,

    /// <summary>A quote has gone out.</summary>
    Quoted = 2,

    /// <summary>Talking it through.</summary>
    Negotiating = 3,

    /// <summary>Booked.</summary>
    Won = 4,

    /// <summary>Went elsewhere, or not now. Always with a reason: it is what the agency learns from.</summary>
    Lost = 5,
}

/// <summary>How a lead reached the agency.</summary>
public enum LeadSource
{
    /// <summary>The trip-request form on the agency's storefront.</summary>
    TripRequestWidget = 1,

    /// <summary>The storefront's contact form.</summary>
    ContactForm = 2,

    /// <summary>Keyed in by someone at the agency: a phone call, a walk-in, a WhatsApp message.</summary>
    Manual = 3,
}

/// <summary>Where a quote stands with the customer.</summary>
public enum QuoteStatus
{
    /// <summary>Being written. The only status in which a quote can be changed.</summary>
    Draft = 1,

    /// <summary>Emailed to the customer, with a link to it on the agency's storefront.</summary>
    Sent = 2,

    /// <summary>The customer has opened the link.</summary>
    Viewed = 3,

    /// <summary>The customer accepted it on the link.</summary>
    Accepted = 4,

    /// <summary>The customer declined it on the link.</summary>
    Declined = 5,

    /// <summary>Sent, not answered, and past its last valid day. Worked out when read, never stored.</summary>
    Expired = 6,
}

/// <summary>How a message travelled.</summary>
/// <remarks>
/// Only email is ever sent by the platform. SMS and WhatsApp are logged here by the agent, not sent
/// from here — the MVP has no provider for either (build plan, "What the MVP leaves out").
/// </remarks>
public enum CommunicationChannel
{
    Email = 1,

    Sms = 2,

    Whatsapp = 3,

    Call = 4,

    /// <summary>Something worth writing down that was not a message: "deposit received".</summary>
    Note = 5,
}

/// <summary>Which way a message went.</summary>
public enum CommunicationDirection
{
    /// <summary>From the customer to the agency.</summary>
    Inbound = 1,

    /// <summary>From the agency to the customer.</summary>
    Outbound = 2,
}

/// <summary>What a task or a message is about.</summary>
public enum CrmRecordType
{
    Lead = 1,

    Customer = 2,

    Quote = 3,
}
