# Entity configurations

One file per entity, each implementing `IEntityTypeConfiguration<T>`.
`AppDbContext.OnModelCreating` finds them automatically — you never register one by hand.

```csharp
internal sealed class AgencyConfiguration : IEntityTypeConfiguration<Agency>
{
    public void Configure(EntityTypeBuilder<Agency> builder)
    {
        builder.ToTable("agencies");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.LegalName).HasMaxLength(200).IsRequired();
    }
}
```

**Never put `[Table]`, `[Column]` or `[MaxLength]` on a domain entity.** Those attributes drag
EF Core into `TripsAgent.Domain`, and an architecture test fails the build when that happens —
business rules have to stay testable without a database.

Two things you mostly do not need to spell out:

|                                               | Handled by                                                           |
| --------------------------------------------- | -------------------------------------------------------------------- |
| Table and column names in `snake_case`        | `UseSnakeCaseNamingConvention()`                                     |
| `*Minor` money properties → `bigint`          | [`MoneyMinorConvention`](../Conventions/MoneyMinorConvention.cs)     |
| `DateTimeOffset` → `timestamp with time zone` | [`UtcTimestampConvention`](../Conventions/UtcTimestampConvention.cs) |

A new tenant-scoped table also needs `agency_id` and a global query filter — see CLAUDE.md rule 3.
