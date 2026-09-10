namespace TripsAgent.Domain.Auditing;

/// <summary>
/// Marks an entity whose every insert, update and delete is written to the platform audit log.
///
/// Not to be confused with <c>IAuditableEntity</c>, which only stamps <c>CreatedAt</c> and
/// <c>UpdatedAt</c> on the row itself. That answers "when did this last change"; this answers
/// "who changed it, from what, to what, and why" — and keeps the answer after the row is gone.
///
/// Opting in is the whole mechanism: the persistence layer watches for this interface, so an
/// entity that implements it cannot be changed without a record appearing, and nobody has to
/// remember to call anything. Add it to anything touching money, identity, or an agency's
/// standing — the things somebody will one day have to reconstruct.
///
/// It declares no members. An entity does not have to change shape to be audited.
/// </summary>
public interface IAuditLogged;
