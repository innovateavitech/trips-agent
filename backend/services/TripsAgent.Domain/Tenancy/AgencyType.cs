namespace TripsAgent.Domain.Tenancy;

/// <summary>Where an agency sits in the hierarchy.</summary>
public enum AgencyType
{
    /// <summary>
    /// A travel business that signed up with us directly. Sits at the root of its own tree and
    /// may manage sub-agents beneath it.
    /// </summary>
    Principal = 1,

    /// <summary>
    /// An agency managed by a principal. It is a full agency row, not merely a scoped user: it
    /// has its own wallet allowance, its own markup rules and eventually its own storefront.
    /// It inherits branding from its parent by default.
    /// </summary>
    SubAgent = 2,
}
