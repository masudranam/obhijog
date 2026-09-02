# Rules — EF Core, migrations and data access

Applies to `src/MunicipalSla.Infrastructure/`.
Read [SPEC.md §8](../../SPEC.md#8-data-model), [§8.12](../../SPEC.md#812-postgresql-specifics) and
[§9](../../SPEC.md#9-authorization--data-scoping) alongside this.

## PostgreSQL, not SQL Server

Provider is **Npgsql**. The traps, in the order you will hit them:

| Trap | What to do |
|---|---|
| `rowversion` does not exist | `UseXminAsConcurrencyToken()` — see Concurrency below |
| PascalCase identifiers need quoting in every hand-written statement | `UseSnakeCaseNamingConvention()` globally; never `HasColumnName` per property (SPEC D11) |
| `HasFilter` takes **raw SQL** and the naming convention does not rewrite it | write partial-index filters in snake_case yourself: `HasFilter("assigned_staff_id IS NOT NULL")`. Getting this wrong produces a migration that fails only on apply. |
| `timestamptz` rejects a non-UTC `DateTimeOffset` | everything comes from `TimeProvider.GetUtcNow()`; never construct a local-offset value |
| comparison is case-sensitive | `EF.Functions.ILike(...)` for the `q` filter. Not `LIKE` over `.ToLower()` — that cannot use an index |
| no `tinyint` | `smallint` for `EscalationLevel` |
| `bit` is not a boolean | `boolean`, with `true` / `false` |
| `varchar(n)` and `text` store identically | keep the declared lengths anyway — they are validation, not optimisation |

Ids are `Guid.CreateVersion7()` **in the application**. No `gen_random_uuid()` column default: it
would add an extension dependency and force a round trip to learn the id you just inserted.

Enums are stored as `varchar`, never as native PostgreSQL `enum` types — adding a value to a native
enum needs a migration, which is friction we do not want on `Notification.Type`.

## Migrations are forward-only

Once a migration is committed it is **immutable**. Fix a bad one by writing another:

```bash
dotnet ef migrations add <Name> \
  --project src/MunicipalSla.Infrastructure --startup-project src/MunicipalSla.Api
```

Never edit, never delete, never `migrations remove` a committed migration. The guard hook blocks
deleting or moving anything under `Migrations/`, and it is right to.

`dotnet ef migrations remove` is legitimate for exactly one case: a migration you generated in this
session and have not committed. Say so out loud when you do it.

Every schema change lands with its migration **in the same commit**. An entity change without a
migration is a broken build for the next person.

## Configuration lives in `IEntityTypeConfiguration<T>`

One class per entity under `Infrastructure/Persistence/Configurations/`. No fluent configuration
inline in `OnModelCreating` beyond `ApplyConfigurationsFromAssembly`, and no data annotations on
domain entities — the domain layer stays free of persistence concerns.

Each configuration must state explicitly: column types and lengths, required/optional,
delete behaviour on every relationship, check constraints, and the indexes from SPEC §8.

## The indexes are not optional

SPEC §8.4 lists five indexes on `Complaint`, each tied to a query that actually runs. The partial
`(Status, SlaDueAt)` index is the one the sweeper depends on — without it the sweep table-scans
every complaint every 60 seconds.

Adding a query pattern means adding or justifying an index in the same PR, and updating SPEC §8.4.

## One scoping seam

```csharp
public static IQueryable<Complaint> For(IQueryable<Complaint> source, CurrentUser user)
```

**Every** complaint query starts here. Not "most". A hand-rolled
`Where(c => c.CitizenId == user.Id)` anywhere else is a review finding *even when it is correct*,
because the next one will not be, and there is no way to test a rule that lives in fifteen places.

Concretely:

- `Citizen` → `CitizenId == user.Id`
- `Staff` / `DeptAdmin` → `DepartmentId == user.DepartmentId`
- a `departmentId` query parameter never widens scope; it is ignored unless it matches the caller's
  own department

**Out of scope throws `NotFoundException`**, which maps to `404`. Never `ForbiddenException` for a
row the caller cannot see (SPEC §9.2). The list endpoint and the CSV export share this code — a
divergence between them is a bug, not a feature.

`IsInternal` comments are filtered **in the query** for a `Citizen`, never in a DTO mapper and never
in the UI.

## Read paths are projections

All list and detail reads are `AsNoTracking()` projections straight to DTOs:

```csharp
ComplaintQueryScope.For(db.Complaints, user)
    .AsNoTracking()
    .Where(...)
    .OrderByDescending(c => c.CreatedAt)
    .Select(c => new ComplaintListItem { ... })
```

Never load an entity graph with `Include` chains to serialise it. Never return an EF entity from an
endpoint.

Write paths do track, and load only what they mutate.

## Concurrency

`Complaint.Version` maps PostgreSQL's system `xmin` column as the concurrency token, configured once
with `entity.UseXminAsConcurrencyToken()`. There is no `rowversion` in PostgreSQL and there is no
extra column here — every `UPDATE` bumps `xmin` for free.

`Version` is a `uint` and is **never exposed in a DTO**. Clients do not send it back; the concurrency
window is the single request that reads and writes inside one transaction.

A conflicting concurrent transition must lose: catch `DbUpdateConcurrencyException` at the handler
and return `409` (SPEC §12.4). Do not retry silently — the caller's decision was made against stale
state and they need to see it.

## Transactions

A transition is one transaction: the complaint mutation, its `ComplaintStatusHistory` row, and any
`EscalationEvent` and `Notification` rows. Either all of it lands or none of it does.

The sweeper transacts **per complaint**, not per pass. One failing complaint must not roll back the
199 that succeeded, and a pass that dies halfway must leave a consistent database.

## The sweeper's three defences

SPEC §11.3 names three mechanisms and they are not redundant:

1. the `WHERE` clause that selects only work not yet done (`SlaWarnedAt IS NULL`, …)
2. one transaction per complaint, marker written with the escalation rows
3. the unique index on `(ComplaintId, ReopenCount, Level)`

**Never remove one because the others cover it.** (1) is correctness, (2) is atomicity, (3) is the
backstop for two overlapping sweeps — the case (1) cannot see. Deleting any one of them is the
mutation the reviewer will try first.

## The seeder

Idempotent means: match on natural keys (`Department.Code`, `ComplaintCategory.Name`, `User.Email`),
insert only what is missing, and leave identical row counts when run twice.

- No `EnsureDeleted`, no `Database.EnsureCreated` — the seeder runs *after* migrations.
- **No password literal.** `SEED_PASSWORD` comes from the environment and the seeder **throws** when
  it is absent. A default development password that reaches a deployed environment is a real
  vulnerability, not a convenience.
- Seed data is listed in SPEC §8.11; changing it means changing that table in the same commit.
- The M7 overdue complaints are computed backwards from `TimeProvider.GetUtcNow()`, so they are
  still overdue whenever the seeder runs.
