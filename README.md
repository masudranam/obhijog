# Obhijog — Municipal Complaint & SLA Tracking

**অভিযোগ** — *obhijog*, Bangla for "complaint".

A civic complaint desk with SLA tracking and automatic escalation.

Citizens report civic issues — a broken streetlight, a water leak, uncollected garbage — with a
photo and a location. Each complaint is routed to a department by its category, which also fixes the
SLA window for that kind of issue. A department admin assigns it to staff, who work it and resolve
it. Every complaint runs against a deadline, and when that deadline is missed the system escalates
automatically and notifies the people responsible.

**Stack:** .NET 9 Minimal APIs · EF Core 9 · Azure Database for PostgreSQL · Angular 20 (standalone + signals +
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

**M9 of 10 is done.** The solution skeleton, CI gate, Angular shell, compose stack, health
endpoints, the full data model with its migration, an idempotent seeder, JWT authentication with
the three roles, complaint submission and photo attachments are in place: a citizen files a
complaint with photos, gets a reference number and an SLA deadline, and anyone holding that
reference can track it without an account.

Complaints now move. The twelve-row guard table of
[SPEC §12.3](SPEC.md#123-the-guard-table) drives every status change through one endpoint, a
department admin triages and assigns from the inbox, staff work their queue, and each complaint
carries the actions *that* caller may take right now — so the UI renders buttons rather than
re-deriving who may do what. Comments are in, and an internal note never reaches the reporter.

The SLA clock ticks. A sweep runs every minute — and on demand from the breach screen — warning
a complaint at 80% of its window, breaching it at 100%, escalating it again at 150%, and closing a
resolved complaint nobody came back to after seven days. Each rung writes its escalation and its
notifications in one transaction, and **running the sweep twice produces exactly what running it
once did**: that is the property the whole feature rests on and it has its own suite against a
real PostgreSQL. A department admin sees their overdue work on `/breaches`, worst first.

A dashboard totals the work — open, approaching, breached, escalated, and how the last thirty
days went against their deadlines — computed server-side over whatever the caller is allowed to
see, so a citizen gets the same screen over their own complaints. A department admin can export
what they are looking at as CSV, sharing the list's filter and scope code rather than a second
implementation of it, with every field quoted and anything a spreadsheet would treat as a formula
defused first.

There is now something to deploy it with. `infra/main.bicep` stands up the whole footprint — a
Burstable PostgreSQL flexible server, a private blob container, Key Vault, a Container Apps
environment and app, a Static Web App, and a managed identity carrying the role assignments — with
the database reachable only over the VNet. Not "the public endpoint is closed": the server is
VNet-injected, so it has no public endpoint and Azure will not accept a firewall rule against it.
Bicep, however, will happily compile one — so CI greps the compiled template for `0.0.0.0` and for
`firewallRules` on every commit and fails on either. No secret is a literal anywhere: three `@secure()` parameters arrive from the environment, land
in Key Vault, and come back as Key Vault references, and none of them is ever a deployment output.
`.github/workflows/deploy.yml` is dispatch-only and previews with `what-if` before it applies.

**It has not been deployed.** `what-if` needs an authenticated subscription, which this project
does not have. What CI verifies on every SHA is that the template compiles and lints clean, that
the compiled ARM contains no firewall rule, and that the API image builds. That the running
container answers `/health`, `/health/ready` and `401` on a protected route was checked by hand,
once — worth knowing, but it is not a standing check and nothing re-runs it.

[SPEC.md §20](SPEC.md#20-delivery-roadmap) is the live progress tracker — ten milestones, each one
issue and one pull request.

## Getting started

Prerequisites: **.NET 9 SDK**, Node 20+, Docker Desktop.

```bash
docker compose -f infra/docker-compose.yml up -d   # PostgreSQL 17 + Azurite
dotnet run --project src/Obhijog.Api               # http://localhost:5080
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

CI runs exactly this against a real PostgreSQL service container on the commit that will merge.
