namespace TripsAgent.Domain.Platform;

/// <summary>
/// What an erased person's details are replaced with (issue 106).
/// </summary>
/// <remarks>
/// In the domain rather than beside the service that writes them, because a database trigger has to
/// recognise the same values: <c>documents.generated_documents</c> refuses every change to an issued
/// document's recipient except this one. The migration interpolates these constants, so the two
/// cannot drift apart.
/// </remarks>
public static class ErasureDefaults
{
    /// <summary>What a person's name becomes. Deliberately not a blank: a blank looks like a bug.</summary>
    public const string ErasedName = "Erased at request";

    /// <summary>What a traveller's given name becomes.</summary>
    public const string ErasedFirstName = "Erased";

    /// <summary>What a traveller's family name becomes.</summary>
    public const string ErasedLastName = "Traveller";

    /// <summary>What free text about a person becomes.</summary>
    public const string ErasedText = "[erased at the person's request]";
}
