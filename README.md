# Municipal Complaint & SLA Tracking

A civic complaint desk with SLA tracking and automatic escalation.

Citizens report civic issues — a broken streetlight, a water leak, uncollected garbage — with a
photo and a location. Each complaint is routed to a department by its category, which also fixes the
SLA window for that kind of issue. A department admin assigns it to staff, who work it and resolve
it. Every complaint runs against a deadline, and when that deadline is missed the system escalates
automatically and notifies the people responsible.

**Stack:** .NET 9 Minimal APIs · EF Core 9 · Azure SQL · Angular 20 (standalone + signals +
Material) · Azure Blob Storage · Bicep · GitHub Actions

## What is actually interesting here

Not the CRUD. Three things:

1. **One guarded state machine.** A complaint cannot move except through a single twelve-row
   transition table that encodes the target state, who may perform the action, and what data the
   action requires. There is one `POST /complaints/{id}/transitions` endpoint, so no route can
   bypass it.
2. **An idempotent SLA sweeper.** A periodic sweep raises warnings at 80%, breaches at 100% and a
   second escalation level at 150%. It can run any number of times — by timer, by hand, or from a
   redelivered queue message — without ever double-escalating or double-notifying.
3. **Strict role scoping.** Three roles, one department boundary, and the rule that a complaint
   outside your scope is *indistinguishable from one that does not exist* — `404`, never `403`.

## Documentation

| Document | What it is |
|---|---|
| [SPEC.md](SPEC.md) | The authoritative product and technical specification: domain model, state machine, API surface, authorization invariants, and the milestone roadmap. Code that disagrees with it is a bug in one of the two. |
| [CLAUDE.md](CLAUDE.md) | Working rules for agents and contributors — the non-negotiables, the commands, the testing bar. |
| [.claude/skills/feature-cycle](.claude/skills/feature-cycle/SKILL.md) | How one issue goes from pick to merged PR. |
| [docs/adr/](docs/adr/) | Decisions costly to reverse. |

## Roles

| Role | Sees | Does |
|---|---|---|
| **Citizen** | only their own complaints | submit, comment, attach photos, confirm or reopen a resolution, track by reference number without logging in |
| **Staff** | every complaint in their department | start and resolve the ones assigned to them, comment including internal notes |
| **Dept Admin** | every complaint in their department | assign, reassign, recategorize, reject, close; manage categories and SLA hours; manage department staff; dashboard, breach list, CSV export |

## Status

**Nothing is scaffolded yet.** The repository currently holds the specification and the agent
harness. The roadmap table in [SPEC.md §20](SPEC.md#20-delivery-roadmap) is the live progress
tracker — ten milestones, each one issue and one pull request.

## Getting started

Prerequisites: **.NET 9 SDK**, Node 20+, Docker Desktop.

```bash
docker compose -f infra/docker-compose.yml up -d   # SQL Server 2022 + Azurite
dotnet run --project src/MunicipalSla.Api          # http://localhost:5080
npm ci --prefix web && npm start --prefix web      # http://localhost:4200
```

Configuration keys, and the four things that must change together when the API port changes, are in
[SPEC.md §19](SPEC.md#19-configuration--environments). No secret ever lands in a committed file:
`SEED_PASSWORD` comes from the environment, and seeding fails loudly without it rather than planting
a known password.

### The gate

```bash
dotnet format --verify-no-changes
dotnet build --configuration Release      # warnings are errors
dotnet test  --configuration Release
npm ci --prefix web && npm run build --prefix web
```

CI runs exactly this against a real SQL Server service container on the commit that will merge.
