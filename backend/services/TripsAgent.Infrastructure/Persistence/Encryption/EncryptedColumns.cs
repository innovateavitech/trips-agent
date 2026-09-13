namespace TripsAgent.Infrastructure.Persistence.Encryption;

/// <summary>
/// Every column stored as ciphertext, and the purpose each is encrypted under (issue 104).
/// </summary>
/// <remarks>
/// A purpose is the column's own name. It is authenticated with the value, so ciphertext copied into
/// another column will not decrypt there. Never rename one: every value already stored was made under it.
/// </remarks>
public static class EncryptedColumns
{
    /// <summary>A traveller's passport number on an order.</summary>
    /// <remarks>Also the purpose the number was stored under before issue 104, which the backfill reads.</remarks>
    public const string OrderTravellerPassportNumber = "orders.order_travellers.passport_number";

    /// <summary>A traveller's passport expiry on an order.</summary>
    public const string OrderTravellerPassportExpiry = "orders.order_travellers.passport_expiry";

    /// <summary>The number of a passenger's travel document on a supplier booking.</summary>
    public const string PassengerDocumentNumber = "supplier.passenger_documents.doc_number";

    /// <summary>When a passenger's travel document expires.</summary>
    public const string PassengerDocumentExpiry = "supplier.passenger_documents.expires_on";

    /// <summary>An agency's bank account number.</summary>
    public const string AgencyBankAccountNumber = "payments.agency_bank_accounts.account_number";
}
