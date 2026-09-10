# Database migrations

Every change to the database schema lives here, as an EF Core migration, in timestamp order.
This folder is the schema's history — read it top to bottom and you have the whole story.

## Creating one

```bash
./scripts/new-migration.sh AddAgencies
```

Never call `dotnet ef migrations add` directly. These files sit outside the C# project, so the
command needs an output directory and a namespace to match; the script passes both. Get one
wrong and you get migrations that compile but that EF Core cannot find — which fails on the
next person's machine, not on yours.

`services/TripsAgent.Infrastructure/TripsAgent.Infrastructure.csproj` compiles this folder into
the Infrastructure assembly with an explicit `<Compile Include>`.

## Applying them

```bash
dotnet run --project services/TripsAgent.Api -- migrate
```

Applying is a separate command, never something that happens on API startup: two instances
booting at once would race each other through the same schema change.

## Rules

- **Read the generated file before committing it.** EF infers the diff. A property rename it
  does not recognise comes out as a drop plus an add — which silently deletes live data.
- **Commit `AppDbContextModelSnapshot.cs` with your migration.** It records the state the next
  migration starts from. Leave it out and the following migration tries to recreate everything.
- **Never edit a migration that is already on `main`.** Other people's databases have already
  applied it. Write a new one that corrects it.
- **One migration per pull request**, named after what it does — `AddAgencies`, not `Update1`.

CI runs [`scripts/check-migrations.sh`](../../scripts/check-migrations.sh) on every pull request
and fails it if the model has changed with no migration to match.

---

Empty for now. The first migration arrives with the `agencies` schema — issue #10.
