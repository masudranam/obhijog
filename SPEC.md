# Obhijog — Municipal Complaint & SLA Tracking · Product & Technical Specification

> **Status:** v1.0 · **Owner:** Md Masud Rana · **Created:** 2026-09-02
> **Name:** *Obhijog* (অভিযোগ) — Bangla for "complaint".
> **Purpose:** A finishable fullstack portfolio project on .NET 9 + Angular 20 + Azure Database for PostgreSQL.
> This document is the source of truth for *what* gets built and *in what order*.
> **Code that disagrees with this file is a bug in one of the two — fix both.**

---

## Table of contents

1. [Overview](#1-overview)
2. [Goals, learning objectives & non-goals](#2-goals-learning-objectives--non-goals)
3. [Personas & roles](#3-personas--roles)
4. [Glossary](#4-glossary)
5. [Tech stack](#5-tech-stack)
6. [Repository layout](#6-repository-layout)
7. [Architecture](#7-architecture)
8. [Data model](#8-data-model)
9. [Authorization & data scoping](#9-authorization--data-scoping)
10. [Authentication](#10-authentication)
11. [SLA & escalation engine](#11-sla--escalation-engine)
12. [Complaint state machine](#12-complaint-state-machine)
13. [API conventions & surface](#13-api-conventions--surface)
14. [Feature specifications](#14-feature-specifications)
15. [Frontend architecture](#15-frontend-architecture)
16. [Backend architecture](#16-backend-architecture)
17. [Non-functional requirements](#17-non-functional-requirements)
18. [Testing strategy](#18-testing-strategy)
19. [Configuration & environments](#19-configuration--environments)
20. [Delivery roadmap](#20-delivery-roadmap)
21. [Working agreement](#21-working-agreement)
22. [Out of scope](#22-out-of-scope)
23. [Decision log](#23-decision-log)

---

## 1. Overview

A single municipality runs a public complaint desk. A **Citizen** reports a civic issue — a broken
streetlight, a water leak, uncollected garbage — attaching a photo and a location. The complaint is
routed to a **Department** by its category, which also fixes the **SLA window** for that kind of
issue. A **Dept Admin** assigns it to **Staff**, who work it and resolve it. Every complaint runs
against a deadline; when that deadline is missed the system **escalates automatically** and notifies
the people responsible.

The interesting engineering is not the CRUD. It is three things:

1. **A single guarded state machine** — a complaint cannot move except through one transition table
   that encodes the target state, who is allowed to perform the action, and what data the action
   requires (§12).
2. **An idempotent SLA sweeper** — a periodic sweep that raises warnings, breaches and two
   escalation levels, and can be run any number of times without ever double-escalating or
   double-notifying (§11).
3. **Strict role scoping** — three roles, one department boundary, and a rule that a complaint
   outside your scope is **indistinguishable from one that does not exist** (§9).

Those three are also where the tests live (§18).

---

## 2. Goals, learning objectives & non-goals

### Goals

- Ship a complete, demonstrable system in **10 milestones**, each a single merged pull request.
- Exercise the .NET 9 + Angular 20 + Azure Database for PostgreSQL stack end to end, with Blob Storage for photos.
- Demonstrate a real background-processing story (SLA escalation) that starts in-process and has a
  documented, flag-switched path to Service Bus + Azure Functions.

### Learning objectives

| Area | What this project demonstrates |
|---|---|
| Domain modelling | A guarded state machine kept out of controllers and out of EF |
| EF Core 9 | Forward-only migrations, indexed query plans, optimistic concurrency via `xmin` |
| Authorization | Claims-based roles plus a single query-scoping seam, proven by tests |
| Background work | An idempotent sweeper, safe to trigger manually and safe to relocate |
| Azure | Azure Database for PostgreSQL, Blob Storage with short-lived read SAS, Bicep, container build, GitHub Actions |
| Angular 20 | Standalone components, signals, functional guards, lazy feature routes, Material |

### Non-goals

These are **deliberately not built**. They are not oversights; do not add them.

- Multiple municipalities / tenancy. One municipality, one database.
- Business-hours or holiday-aware SLA calendars. The clock is calendar hours (§11).
- Real-time push (SignalR / WebSockets). The UI refreshes on navigation and on action.
- Real email or SMS delivery in the MVP. Notifications are **persisted rows** with a log sink (F12).
- Duplicate detection, clustering of nearby complaints, citizen satisfaction surveys, public
  heat-maps, mobile apps, i18n.
- A super-admin role. Dept Admins come from seed; they manage only their own department.

---

## 3. Personas & roles

Three roles. A user has exactly one.

| Role | Who | `DepartmentId` | Can |
|---|---|---|---|
| `Citizen` | Member of the public | always `null` | Self-register, submit complaints, see **only their own**, comment, attach photos, close or reopen a resolved complaint, track by reference number without logging in |
| `Staff` | Field or desk worker in one department | **required** | Read every complaint in their department, act only on complaints **assigned to them** (`start`, `resolve`), comment including internal notes, attach photos |
| `DeptAdmin` | Department supervisor | **required** | Everything `Staff` can read in their department, plus `assign`, `reassign`, `recategorize`, `reject`, `close`; manage categories and SLA hours; create and deactivate `Staff` in their own department; see the dashboard, the breach list and CSV export |

There is also a pseudo-actor, **`System`**, used only by the SLA sweeper. It performs `close`
(auto-close of long-resolved complaints) and writes escalation and notification rows. It is never a
`User` row and never authenticates.

---

## 4. Glossary

| Term | Meaning |
|---|---|
| **Complaint** | One reported civic issue. The aggregate root. |
| **Reference number** | Human-quotable public identifier, `MC-{year}-{seq:D6}`, e.g. `MC-2026-000123`. |
| **Category** | The kind of issue. Carries **both** the routing target department **and** the SLA hours. |
| **SLA window** | `Category.SlaHours` calendar hours from complaint creation. |
| **`SlaDueAt`** | The absolute deadline. Stored, not computed at read time. |
| **Warning** | 80% of the window elapsed, still open. Advisory; notifies the assignee. |
| **Breach** | 100% of the window elapsed, still open. Sets `SlaBreachedAt`, escalation level 1. |
| **Escalation level 2** | 150% of the window elapsed, still open. Notifies the Dept Admins again. |
| **Sweep** | One pass of the SLA engine over open complaints. Idempotent by construction. |
| **Transition** | A named action moving a complaint between statuses, validated by the guard table. |
| **Scope** | The set of complaints a given user may see. Enforced in one place (§9). |

---

## 5. Tech stack

| Layer | Choice | Notes |
|---|---|---|
| API | **.NET 9**, ASP.NET Core **Minimal APIs** grouped per feature | No MediatR — plain injected services. No custom response envelope. |
| ORM | **EF Core 9** + **Npgsql.EntityFrameworkCore.PostgreSQL 9** | Forward-only migrations. `xmin` concurrency token on `Complaint` (§8.12). |
| Database | **Azure Database for PostgreSQL flexible server** / PostgreSQL 17 locally via Docker | One database, no tenancy. `snake_case` naming via `EFCore.NamingConventions` (D11). |
| Blob | **Azure Blob Storage**, **Azurite** locally | Container `complaint-attachments`, private; reads via short-lived SAS. |
| Errors | **RFC 9457 `ProblemDetails`** | One exception-to-status mapper, no per-endpoint `try/catch`. |
| Auth | **ASP.NET Core Identity** + self-issued **JWT** (access + refresh) | Role as a claim. Not Entra — see D2 in §23. |
| Background | **`BackgroundService`** hosted in the API | Flag-switchable to Service Bus + Functions (M10). |
| Web | **Angular 20**, standalone components, signals, functional guards, lazy routes | **Angular Material** for the component kit. |
| Map | **Leaflet** + OpenStreetMap tiles | No API key, no billing. |
| Tests | **xUnit** | Four focused suites (§18). Zero frontend tests. |
| CI | **GitHub Actions** | `dotnet format` → `build -warnaserror` → `test` → `ng build`. |
| IaC | **Bicep** (M9) | Azure Database for PostgreSQL, Storage, Container App, Static Web App, Key Vault, managed identity. |

### Prerequisites for a local build

- **.NET SDK** — present as **10.0.401** at `C:\Program Files\dotnet`, but **not on `PATH`**, so
  a bare `dotnet` fails and the toolchain reads as absent until the directory is prepended. The
  SDK builds this `net9.0` solution; only the .NET 10 runtime is installed, so running the API or
  the tests needs `DOTNET_ROLL_FORWARD=Major`. CI pins 9.0.x via `actions/setup-dotnet` and is
  therefore the stricter of the two — a local pass is not a substitute for the gate.
- Node 20+ (present: v20.20.0), Docker Desktop, `gh` CLI (present: 2.97.0).
- **Port 5432 is already taken** on this machine by a native PostgreSQL service, which shadows the
  compose container and fails authentication in a way that looks like a wrong password. Set
  `POSTGRES_PORT` in `infra/.env` to something free and match it in `ConnectionStrings:Postgres`.

---

## 6. Repository layout

```
SPEC.md                              this file — authoritative
CLAUDE.md                            agent working rules; points into this file
src/
  Obhijog.Domain/                    entities, enums, state machine, SLA policy
                                     -- zero EF Core, zero ASP.NET references
  Obhijog.Infrastructure/            DbContext, configurations, migrations, seed,
                                     blob storage, notification sink, SLA sweeper,
                                     and the Identity User/Role entities (D13)
  Obhijog.Api/                       Program.cs, endpoint groups, auth, ProblemDetails
tests/
  Obhijog.Tests/                     xUnit -- four suites, see §18
web/                                 Angular 20 workspace
infra/
  docker-compose.yml                 PostgreSQL 17 + Azurite
  main.bicep, main.bicepparam        M9
functions/
  Obhijog.Functions/                 M10 only -- isolated worker
docs/adr/                            architecture decision records
.claude/                             the agent harness -- see CLAUDE.md
.github/workflows/ci.yml             the gate
```

**Dependency direction is one-way:** `Api → Infrastructure → Domain`. `Domain` references nothing.
A `using Microsoft.EntityFrameworkCore` inside `Obhijog.Domain` is a design break, caught by
review; the project simply must not carry the package reference.

---

## 7. Architecture

```
                Angular 20 (web/)  ─── :4200
                        │  Bearer JWT, HttpClient + auth interceptor
                        ▼
        ┌───────────────────────────────────────────────┐
        │  Obhijog.Api  ── :5080/api/v1                 │
        │                                               │
        │  endpoint groups → services → Domain          │
        │                                               │
        │  ┌─────────────────────────────────────────┐  │
        │  │ SlaSweepService : BackgroundService     │  │
        │  │   every Sla:SweepIntervalSeconds        │  │
        │  │   calls ISlaSweeper.SweepAsync()        │  │
        │  └─────────────────────────────────────────┘  │
        └───────────────┬───────────────────┬───────────┘
                        │                   │
              EF Core 9 + Npgsql      Azure.Storage.Blobs
                        ▼                   ▼
              Azure DB for PostgreSQL   Blob: complaint-attachments
               (PostgreSQL 17 local)      (Azurite locally)
```

Three seams matter, because each is the thing a later milestone or a test plugs into:

| Seam | Interface | Why it exists |
|---|---|---|
| SLA sweep | `ISlaSweeper.SweepAsync(CancellationToken)` | The hosted service, the manual `POST /admin/sla/sweep` endpoint, and the sweep tests all call **the same** code path. M10 moves the *caller*, never the logic. |
| Notification delivery | `INotificationSender.SendAsync(Notification)` | The MVP implementation logs. An email provider becomes a swap, not a rewrite. |
| Attachment storage | `IAttachmentStore` | Azurite and Azure Blob differ only in connection string; a test double avoids needing either. |

Also: **`TimeProvider` is injected everywhere time is read.** Nothing calls `DateTimeOffset.UtcNow`
directly outside `Program.cs`. Without this, the SLA tests cannot exist.

---

## 8. Data model

All keys are `uuid`, generated in the application with **`Guid.CreateVersion7()`** (.NET 9) so they
are time-ordered and keep index locality without a database default. All timestamps are
`timestamptz`; the API always reads and writes UTC.

PostgreSQL-specific mappings — the concurrency token, naming, case-insensitive search — are
collected in §8.12 rather than repeated per table.

### 8.1 `User` (extends `IdentityUser<Guid>`)

**Lives in `Obhijog.Infrastructure`, not `Obhijog.Domain`** — see D13. Domain entities reference
users by `Guid` only, never by navigation property.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `Email`, `PasswordHash`, `SecurityStamp`, … | | from Identity |
| `FullName` | varchar(120) | required |
| `Phone` | varchar(24) | optional |
| `Role` | varchar(16) | `Citizen` \| `Staff` \| `DeptAdmin`. Mirrored into the Identity role table; this column is the queryable copy. |
| `DepartmentId` | Guid? | **null for `Citizen`, required for `Staff` and `DeptAdmin`** — a check constraint enforces it |
| `IsActive` | boolean | default true. Inactive users cannot log in and cannot be assigned. |
| `CreatedAt` | timestamptz | |

Indexes: unique `Email` (Identity), `(DepartmentId, Role)` filtered on `IsActive = true`.

### 8.2 `Department`

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `Name` | varchar(80) | unique |
| `Code` | varchar(12) | unique, uppercase, e.g. `WATER` |
| `IsActive` | boolean | |

**Seeded only.** There is no department CRUD API.

### 8.3 `ComplaintCategory`

The routing table and the SLA policy in one row. This is the design's main lever.

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `Name` | varchar(80) | unique |
| `DepartmentId` | Guid | FK → `Department`. Routing target. |
| `SlaHours` | int | > 0, ≤ 8760. The SLA window. |
| `DefaultPriority` | varchar(8) | `Low` \| `Normal` \| `High` \| `Critical` |
| `IsActive` | boolean | An inactive category cannot receive **new** complaints; existing ones keep working. |

Index: `(IsActive, DepartmentId)`.

**Changing `SlaHours` does not retroactively move existing deadlines.** `SlaDueAt` is stored per
complaint at creation. Recategorizing recomputes it (§11.4).

### 8.4 `Complaint` — the aggregate root

| Column | Type | Notes |
|---|---|---|
| `Id` | Guid | PK |
| `ReferenceNumber` | varchar(20) | unique, `MC-{year}-{seq:D6}` |
| `CitizenId` | Guid | FK → `User` |
| `CategoryId` | Guid | FK → `ComplaintCategory` |
| `DepartmentId` | Guid | FK → `Department`. **Denormalized from the category at creation** so that rerouting is an explicit, audited act rather than a side effect of editing a category. |
| `Title` | varchar(140) | required |
| `Description` | varchar(4000) | required |
| `Status` | varchar(16) | see §12 |
| `Priority` | varchar(8) | seeded from the category, editable by DeptAdmin |
| `Latitude` | decimal(9,6) | required, −90…90 |
| `Longitude` | decimal(9,6) | required, −180…180 |
| `AddressText` | varchar(250) | optional free text |
| `AssignedStaffId` | Guid? | FK → `User`. Null unless `Assigned` / `InProgress`. |
| `CreatedAt` | timestamptz | the SLA clock start |
| `SlaDueAt` | timestamptz | stored, not computed |
| `SlaWarnedAt` | timestamptz? | set once per window by the sweeper |
| `SlaBreachedAt` | timestamptz? | set once per window by the sweeper |
| `EscalationLevel` | smallint | 0, 1 or 2 |
| `ResolvedAt` | timestamptz? | **the SLA measurement point** |
| `ClosedAt` | timestamptz? | |
| `RejectionReason` | varchar(500)? | required when `Rejected` |
| `ResolutionNote` | varchar(1000)? | required when `Resolved` |
| `ReopenCount` | int | default 0 |
| `Version` | uint, mapped to `xmin` | optimistic concurrency token, see §8.12 |

Indexes, chosen for the queries that actually run:

| Index | Serves |
|---|---|
| unique `ReferenceNumber` | public tracking lookup |
| `(CitizenId, CreatedAt desc)` | "my complaints" |
| `(DepartmentId, Status, SlaDueAt)` | department inbox + SLA filters |
| `(AssignedStaffId, Status)` partial, `assigned_staff_id IS NOT NULL` | staff queue |
| `(Status, SlaDueAt)` partial, open statuses only | **the sweep** — this one is load-bearing |

### 8.5 `ComplaintStatusHistory`

Append-only. Never updated, never deleted.

`Id`, `ComplaintId`, `FromStatus`, `ToStatus`, `Action`, `ChangedById` (null when `IsSystem`),
`ChangedAt`, `Note` (varchar(1000), nullable), `IsSystem` (boolean).

Index: `(ComplaintId, ChangedAt)`.

A row is written for **every** accepted transition, including `recategorize` (where
`FromStatus == ToStatus` is legal), the separate priority edit, and system auto-close.

### 8.6 `ComplaintComment`

`Id`, `ComplaintId`, `AuthorId`, `Body` (varchar(2000)), `IsInternal` (boolean), `CreatedAt`.

`IsInternal = true` is **never** returned to a `Citizen`, and only `Staff` / `DeptAdmin` may create
one. Index `(ComplaintId, CreatedAt)`.

### 8.7 `ComplaintAttachment`

`Id`, `ComplaintId`, `BlobName` (varchar(200), unique), `OriginalFileName` (varchar(200)),
`ContentType` (varchar(80)), `SizeBytes` (bigint), `UploadedById`, `UploadedAt`.

`BlobName` is `{complaintId}/{attachmentId}{ext}` — **never** the user-supplied filename.
Index `(ComplaintId, UploadedAt)`.

### 8.8 `EscalationEvent`

`Id`, `ComplaintId`, `ReopenCount`, `Level` (smallint, 1 or 2), `RaisedAt`,
`Reason` (varchar(200)), `NotifiedUserId` (Guid?).

**Unique index `(ComplaintId, ReopenCount, Level)`.** This is the database-level backstop that makes
double-escalation impossible even if two sweeps overlap. The sweeper's `WHERE` clause is the first
line of defence; this index is the second. `ReopenCount` is in the key because a reopened complaint
legitimately re-runs the ladder while its earlier escalation history is preserved (§11.4).

### 8.9 `Notification`

`Id`, `RecipientId`, `ComplaintId` (Guid?), `Type` (varchar(40)), `Subject` (varchar(160)),
`Body` (varchar(2000)), `CreatedAt`, `SentAt` (timestamptz?), `Attempts` (int),
`LastError` (varchar(500)?).

Types: `ComplaintSubmitted`, `ComplaintAssigned`, `ComplaintResolved`, `SlaWarning`, `SlaBreached`,
`SlaEscalatedLevel2`, `ComplaintReopened`, `ComplaintRejected`.

Index `(RecipientId, CreatedAt desc)`, plus a partial `(SentAt)` where `sent_at IS NULL`. Writing the row
and delivering it are separate concerns — the row is the record, delivery is best-effort (F12).

### 8.10 Reference number generation

A PostgreSQL sequence created by migration:

```sql
CREATE SEQUENCE complaint_reference_seq START WITH 1 INCREMENT BY 1;
```

The reference is `MC-{CreatedAt:yyyy}-{nextval:D6}`, where the number comes from
`SELECT nextval('complaint_reference_seq')` issued in the same transaction as the insert.

The sequence is **global, not per-year** — never reset, so two complaints can never collide even
across a year boundary, and no read-modify-write race exists. The year in the string is
informational (D8, §23).

`nextval` is exempt from transaction rollback by design: an abandoned insert burns a number and
leaves a gap. **Gaps are expected and are not a defect** — the reference is an identifier, not a
count.

### 8.11 Seed data (M2, idempotent)

Idempotent means: safe to run against a populated database, matching on natural keys
(`Department.Code`, `ComplaintCategory.Name`, `User.Email`), inserting only what is missing.

- 3 departments: `WATER` (Water & Sewerage), `ELEC` (Street Lighting & Electrical),
  `SANI` (Sanitation & Waste).
- 9 categories:

| Category | Dept | SLA hours | Default priority |
|---|---|---|---|
| Water leak — main | `WATER` | 8 | Critical |
| Water leak — household supply | `WATER` | 48 | Normal |
| No water supply | `WATER` | 24 | High |
| Broken streetlight | `ELEC` | 72 | Normal |
| Exposed electrical cable | `ELEC` | 4 | Critical |
| Traffic signal fault | `ELEC` | 12 | High |
| Uncollected garbage | `SANI` | 24 | Normal |
| Illegal dumping | `SANI` | 72 | Low |
| Blocked drain | `SANI` | 24 | High |

- Per department: 1 `DeptAdmin`, 2 `Staff`. Plus 2 `Citizen`s.
- Emails are `<role><n>.<deptcode>@example.test`. The shared development password comes from the
  `SEED_PASSWORD` environment variable and has **no default** — seeding fails loudly rather than
  planting a known password. Never a literal in source.
Seeding is invoked deliberately and never as a startup side effect:

```bash
dotnet run --project src/Obhijog.Api -- --seed
```

It runs *after* migrations and exits without serving. An app that seeded on boot would race a
rolling deployment.

- **M7 additionally seeds three deliberately overdue complaints** (one past 80%, one past 100%, one
  past 150%) so the escalation ladder is demonstrable without waiting real hours.

### 8.12 PostgreSQL specifics

Collected here so they are decided once rather than rediscovered per entity.

| Concern | Decision |
|---|---|
| **Concurrency token** | PostgreSQL has no `rowversion`. `Complaint.Version` is a `uint` mapped to the system `xmin` column via Npgsql's `UseXminAsConcurrencyToken()`. No extra column, no trigger, and every `UPDATE` bumps it for free. It is never exposed in a DTO. |
| **Naming** | `snake_case` throughout — tables, columns, indexes, constraints — applied globally by `EFCore.NamingConventions` (`UseSnakeCaseNamingConvention`). Never hand-written per property. Entity and property names stay PascalCase in C#; the spec's tables name the **C# property**, and the column is its snake_case form (`SlaDueAt` → `sla_due_at`). |
| **Identifiers** | `uuid` columns, values from `Guid.CreateVersion7()` in the application. No `gen_random_uuid()` default, so no extension dependency and no round trip to learn the id. |
| **Timestamps** | `timestamptz` everywhere, holding `DateTimeOffset`. Npgsql requires the offset to be UTC for `timestamptz`, which suits a system whose clock is UTC by rule (§11.1) — but it means a non-UTC `DateTimeOffset` throws rather than converting. `TimeProvider.GetUtcNow()` is the only source. |
| **Booleans** | `boolean`, with `true` / `false` literals. Never `0` / `1`. |
| **`EscalationLevel`** | `smallint` — PostgreSQL has no `tinyint`. |
| **Partial indexes** | The filtered indexes of §8.4 become PostgreSQL partial indexes via `HasFilter("...")`, written in **snake_case with PostgreSQL syntax** (`"assigned_staff_id" IS NOT NULL`), because `HasFilter` takes raw SQL and the naming convention does not rewrite it. This is the one place the convention will not save you. |
| **Case-insensitive search** | PostgreSQL comparison is case-sensitive. The `q` filter of §13.3 uses `ILIKE` (`EF.Functions.ILike`), not `LIKE` with `ToLower()`, which would defeat any index. |
| **Enums** | Stored as `varchar`, not PostgreSQL `enum` types. A native enum needs a migration to add a value, which is exactly the friction we do not want on `Notification.Type`. |
| **`text` vs `varchar(n)`** | Lengths are declared and enforced as stated in §8. PostgreSQL treats `varchar(n)` and `text` identically in storage, so the length is validation, not optimisation — which is reason to keep it, not to drop it. |

---

## 9. Authorization & data scoping

> **§9 is an invariant section. A violation of anything here is a merge blocker (§21.7).**

### 9.1 The rules

| Role | May read | May act on |
|---|---|---|
| `Citizen` | complaints where `CitizenId == me` | own complaints, actions `close` / `reopen` only |
| `Staff` | complaints where `DepartmentId == my department` | complaints where `AssignedStaffId == me`, actions `start` / `resolve` only |
| `DeptAdmin` | complaints where `DepartmentId == my department` | all department complaints, actions `assign` / `reassign` / `recategorize` / `reject` / `close` |

No role reads across departments. There is no super-admin.

### 9.2 Out of scope is indistinguishable from non-existent

**A complaint the caller may not read returns `404 Not Found`, never `403 Forbidden`.**

A `403` confirms the row exists. This applies to every complaint-addressed route — `GET`,
`transitions`, `priority`, `history`, `comments`, `attachments`, `escalations`.

`403` **is** correct for exactly one situation: the caller can read the complaint but is not
permitted the *action* — a `Staff` attempting `assign`, or a `Staff` attempting `resolve` on a
complaint assigned to a colleague. The distinction:

- cannot see it → `404`
- can see it, cannot do it → `403`

### 9.3 One enforcement point

Every complaint query is built from:

```csharp
IQueryable<Complaint> ComplaintQueryScope.For(IQueryable<Complaint> source, CurrentUser user)
```

in `Obhijog.Infrastructure`. No endpoint filters on `CitizenId` or `DepartmentId` by hand. A
hand-rolled scope filter inside an endpoint is a review finding **even when it happens to be
correct**, because the next one will not be.

Action permission is a separate concern and lives in the state machine's guard table (§12), not
here.

### 9.4 Internal comments

`ComplaintComment.IsInternal = 1` is filtered out for a `Citizen` **in the query** — not in the
serializer and not in the UI. `POST /comments` with `isInternal: true` from a `Citizen` is `403`.

### 9.5 The public tracking endpoint

`GET /api/v1/complaints/by-reference/{ref}` is the single unauthenticated read. It returns a
**redacted projection** and nothing else:

reference number, category name, department name, status, `createdAt`, `slaDueAt`, whether the SLA
is breached, `resolvedAt`, and a public status history carrying only `ChangedAt` and `ToStatus`.

It **never** returns: citizen identity or contact details, staff identity, comments of any kind,
attachments, coordinates, or internal notes. A reference number is guessable enough that this
projection *is* the security boundary.

---

## 10. Authentication

### 10.1 Flows

| Endpoint | Behaviour |
|---|---|
| `POST /auth/register` | **Citizen only.** Ignores any supplied role or department. Returns tokens. |
| `POST /auth/login` | Email + password → access + refresh token. `IsActive = false` → `401`. |
| `POST /auth/refresh` | Rotates: the presented refresh token is revoked and a new pair issued. Reuse of an already-revoked token revokes the whole family and returns `401`. |
| `POST /auth/logout` | Revokes the presented refresh token. |
| `GET /auth/me` | The caller's id, email, name, role, department. |

`Staff` and `DeptAdmin` accounts are **never self-registered** — they come from seed or from
`POST /users` by a `DeptAdmin` in their own department.

### 10.2 Tokens

- **Access token:** JWT, HS256, `Jwt:AccessTokenMinutes` (default **15**). Claims: `sub` (user id),
  `email`, `name`, `role`, and `dept` (department id, absent for a Citizen).
- **Refresh token:** opaque 256-bit random value, stored **hashed** (SHA-256) in a `RefreshToken`
  table (`Id`, `UserId`, `TokenHash`, `ExpiresAt`, `CreatedAt`, `RevokedAt?`, `ReplacedByHash?`);
  `Jwt:RefreshTokenDays` default **14**.
- The signing key comes from configuration and has **no default**. A missing or under-length
  (< 32 byte) `Jwt:SigningKey` **fails startup** — it never falls back to a built-in value.

### 10.3 Authorization wiring

Policies, not role strings scattered through endpoint files: `Policies.Citizen`, `Policies.Staff`,
`Policies.DeptAdmin`, `Policies.StaffOrAdmin`. Endpoints call `.RequireAuthorization(Policies.X)`.
The `dept` claim is read once per request into a scoped `CurrentUser` service, which is what
`ComplaintQueryScope` consumes.

---

## 11. SLA & escalation engine

### 11.1 The clock

```
SlaDueAt  = CreatedAt + Category.SlaHours          (calendar hours, UTC)
elapsed%  = (now − CreatedAt) / (SlaDueAt − CreatedAt) × 100
```

**Calendar hours. The clock never pauses.** There is no `OnHold` status, no business-hours
calendar, no holiday table. This is a simplification, recorded as D4 in §23 — it is not a gap to be
quietly filled in later.

The SLA is measured to **`ResolvedAt`**, not `ClosedAt`. A complaint resolved inside its window and
closed a week later met its SLA.

### 11.2 The ladder

| Threshold | Marker set | `EscalationLevel` | Notifications |
|---|---|---|---|
| 80% elapsed, still open | `SlaWarnedAt` | unchanged | `SlaWarning` → the assignee, or every active Dept Admin if unassigned |
| 100% elapsed, still open | `SlaBreachedAt` | → `1` | `SlaBreached` → the assignee **and** every active Dept Admin of the department; one `EscalationEvent(Level = 1)` |
| 150% elapsed, still open | — | → `2` | `SlaEscalatedLevel2` → every active Dept Admin; one `EscalationEvent(Level = 2)` |

"Still open" means `Status ∈ { New, Assigned, InProgress }`. `Resolved`, `Closed` and `Rejected`
complaints are outside the sweep entirely.

### 11.3 Idempotency — the load-bearing property

The sweep runs every 60 seconds by default, can be triggered by hand, and will one day be triggered
by a Service Bus message that may be redelivered. It must therefore be **safe to run any number of
times**. Three mechanisms, in order:

1. **The query only selects work that has not been done.** Warnings select
   `SlaWarnedAt IS NULL AND now >= CreatedAt + 0.8 × window`. Breaches select
   `SlaBreachedAt IS NULL AND now >= SlaDueAt`. Level 2 selects
   `EscalationLevel < 2 AND now >= CreatedAt + 1.5 × window`.
2. **The marker is written in the same transaction as the escalation and notification rows.**
   Either all of it lands or none of it does.
3. **`EscalationEvent` is uniquely indexed on `(ComplaintId, ReopenCount, Level)`.** If two sweeps
   ever overlap, the second one's insert fails and its transaction rolls back — instead of
   producing a duplicate.

Consequences that are **required behaviour**, each one a test:

- Running the sweep twice over the same overdue complaint produces **one** `EscalationEvent` per
  level and **one** notification per recipient per level.
- A complaint that crosses 80% and 100% between two sweeps gets **both** markers in one pass, in
  ladder order.
- A complaint already at level 2 is never selected again.
- A `Resolved`, `Closed` or `Rejected` complaint is never selected.

### 11.4 Recategorization and reopening

| Event | Effect on the clock |
|---|---|
| `recategorize` | `SlaDueAt = CreatedAt + newCategory.SlaHours` — recomputed from the **original** creation time, not from now. Markers are **not** cleared: a complaint already breached stays breached. Rerouting is not a way to erase a missed deadline. |
| `reopen` | A fresh window. `CreatedAt` is **not** changed, but `SlaDueAt = now + category.SlaHours`. `SlaWarnedAt`, `SlaBreachedAt`, `EscalationLevel`, `ResolvedAt` and `ResolutionNote` are cleared, and `ReopenCount` is incremented. Existing `EscalationEvent` rows are **kept** — history is never rewritten — which is exactly why `ReopenCount` is part of the unique key: the ladder may legitimately run again for the new window. |
| `Category.SlaHours` edited | No effect on any existing complaint. |

### 11.5 Auto-close

A complaint `Resolved` for more than `Sla:AutoCloseAfterDays` (default **7**) is closed by the
sweeper as the `System` actor: `Status = Closed`, `ClosedAt = now`, plus one
`ComplaintStatusHistory` row with `IsSystem = true` and the note
`"Auto-closed after 7 days without citizen response."`

### 11.6 Batching and safety

- `Sla:SweepBatchSize` (default **200**) caps rows per phase per pass. The sweep **logs when it hits
  the cap** so a backlog is visible rather than silent.
- An exception in one phase is logged and the pass ends; the next pass retries. The sweep never
  takes the API process down.
- One structured log line per pass: examined, warned, breached, escalated, auto-closed, duration.

---

## 12. Complaint state machine

### 12.1 Statuses

`New` → `Assigned` → `InProgress` → `Resolved` → `Closed`, plus `Rejected`.
**`Closed` and `Rejected` are terminal** — no transition leaves them.

| Status | Meaning |
|---|---|
| `New` | Submitted, awaiting triage. SLA clock running. |
| `Assigned` | A staff member owns it. Not started. |
| `InProgress` | Work has started. |
| `Resolved` | Staff declares it fixed, with a resolution note. **SLA measured here.** |
| `Closed` | Terminal. Citizen confirmed, Dept Admin closed, or auto-closed after 7 days. |
| `Rejected` | Terminal. Duplicate, invalid, or not municipal responsibility, with a reason. |

### 12.2 Actions

`assign`, `reassign`, `recategorize`, `reject`, `start`, `resolve`, `close`, `reopen`.

Eight, and the list is closed. The API payload, the guard table, the history `Action` column and the
UI buttons all use exactly these names.

### 12.3 The guard table

This **is** the specification of allowed movement. It lives in
`src/Obhijog.Domain/Complaints/ComplaintStateMachine.cs` as a static readonly dictionary keyed
by `(ComplaintStatus From, ComplaintAction Action)`.

| # | From | Action | To | Allowed roles | Assignee-only | Required payload |
|---|---|---|---|---|---|---|
| 1 | `New` | `assign` | `Assigned` | DeptAdmin | — | `assigneeId` |
| 2 | `New` | `recategorize` | `New` | DeptAdmin | — | `categoryId` |
| 3 | `New` | `reject` | `Rejected` | DeptAdmin | — | `note` |
| 4 | `Assigned` | `reassign` | `Assigned` | DeptAdmin | — | `assigneeId` |
| 5 | `Assigned` | `recategorize` | `New` | DeptAdmin | — | `categoryId` |
| 6 | `Assigned` | `start` | `InProgress` | Staff | **yes** | — |
| 7 | `Assigned` | `reject` | `Rejected` | DeptAdmin | — | `note` |
| 8 | `InProgress` | `resolve` | `Resolved` | Staff | **yes** | `note` |
| 9 | `InProgress` | `reassign` | `Assigned` | DeptAdmin | — | `assigneeId` |
| 10 | `InProgress` | `reject` | `Rejected` | DeptAdmin | — | `note` |
| 11 | `Resolved` | `close` | `Closed` | Citizen (owner), DeptAdmin, System | — | — |
| 12 | `Resolved` | `reopen` | `Assigned` | Citizen (owner), DeptAdmin | — | `note` |

Twelve rows. Anything not in this table is rejected: a `(From, Action)` pair that is absent →
`409 Conflict` with a `ProblemDetails` naming the current status and the actions that *are*
available.

### 12.4 Rules the table does not capture

Enforced alongside it, in `ComplaintTransitionService`:

- **`recategorize` always lands in `New` and clears `AssignedStaffId`**, even when the new category
  belongs to the same department. Uniform beats clever (D5, §23).
- `assigneeId` must be an **active `Staff` user in the complaint's department**, else `400`. After a
  `recategorize` the department may have changed, so this is checked against the *current*
  `DepartmentId`.
- `reassign` to the user who is already the assignee is `400`, not a no-op history row.
- `reopen` applies the clock reset of §11.4 and increments `ReopenCount`.
- `close` by a `Citizen` requires ownership; by a `DeptAdmin`, department scope; by `System`, the
  auto-close age condition.
- Every accepted transition writes exactly one `ComplaintStatusHistory` row **in the same
  transaction** as the mutation. A concurrent conflicting transition loses on `xmin` and gets
  `409`.
- **`Priority` is not a transition.** `PUT /complaints/{id}/priority` (DeptAdmin) is a separate,
  status-independent edit that also writes a history row with `Action = "priority"`.

### 12.5 Diagram

```
                    ┌──── reject (DeptAdmin, note) ──────────────┐
                    │                                            │
  ┌─ recategorize ──┤                                            ▼
  │  (clears        │                                        Rejected
  │   assignee)     │                                            ▲
  ▼                 │                                            │
 New ──assign──► Assigned ──start──► InProgress ──resolve──► Resolved
  ▲                 │  ▲                 │  │                 │   │
  │                 │  │                 │  └─── reject ──────┘   │
  └── recategorize ─┘  └─── reassign ────┘                        │
                       │                                          │
                       └────────── reopen (note) ─────────────────┤
                                                                  │
                                       close (Citizen | DeptAdmin │
                                              | System)           │
                                                                  ▼
                                                               Closed
```

Both `reject` arrows and the `reassign` arrow from `InProgress` are DeptAdmin-only; `start` and
`resolve` are the assignee's alone.

---

## 13. API conventions & surface

### 13.1 Conventions

- Base path **`/api/v1`**. Version in the path; no header negotiation.
- **Errors: RFC 9457 `ProblemDetails`** on every non-2xx, with `type`, `title`, `status`, `detail`,
  `traceId`, and for validation failures an `errors` dictionary. No custom envelope, no
  `{ "success": false }`.
- **Success responses are the resource or the page**, not a wrapper. Pages are
  `{ "items": [...], "page": 1, "pageSize": 20, "total": 137 }`.
- `page` is 1-based; `pageSize` default 20, max 100. Over the max → `400`, not a silent clamp.
- Timestamps are ISO-8601 with offset (`2026-09-02T14:31:00+00:00`).
- Enums cross the wire as **strings** (`"InProgress"`), accepted case-insensitively, emitted
  PascalCase.
- Status codes: `200` read · `201` + `Location` create · `204` no-content mutate · `400` validation
  · `401` unauthenticated · `403` visible-but-forbidden · `404` out of scope or absent · `409`
  illegal transition or concurrency conflict · `413` attachment too large · `415` bad content type.
  `422` is never used.

### 13.2 The surface

```
── auth ─────────────────────────────────────────────────────────────────────────
POST   /api/v1/auth/register                 anon       Citizen self-registration
POST   /api/v1/auth/login                    anon
POST   /api/v1/auth/refresh                  anon       rotating refresh
POST   /api/v1/auth/logout                   any
GET    /api/v1/auth/me                       any

── reference data ───────────────────────────────────────────────────────────────
GET    /api/v1/departments                   any
GET    /api/v1/departments/{id}/staff        DeptAdmin  own department only
GET    /api/v1/categories                    any        ?includeInactive= (admin only)
POST   /api/v1/categories                    DeptAdmin  own department only
PUT    /api/v1/categories/{id}               DeptAdmin  own department; how SLA hours change
POST   /api/v1/users                         DeptAdmin  creates Staff in own department
PUT    /api/v1/users/{id}/active             DeptAdmin  activate / deactivate own-dept Staff

── complaints ───────────────────────────────────────────────────────────────────
POST   /api/v1/complaints                    Citizen    → 201, reference number
GET    /api/v1/complaints                    any        role-scoped list, see 13.3
GET    /api/v1/complaints/{id}               any        role-scoped; out of scope → 404
GET    /api/v1/complaints/by-reference/{ref} anon       redacted projection, §9.5
POST   /api/v1/complaints/{id}/transitions   any        { action, note?, assigneeId?, categoryId? }
PUT    /api/v1/complaints/{id}/priority      DeptAdmin
GET    /api/v1/complaints/{id}/history       any        role-scoped
GET    /api/v1/complaints/{id}/comments      any        IsInternal filtered for Citizen
POST   /api/v1/complaints/{id}/comments      any        isInternal requires Staff/DeptAdmin
GET    /api/v1/complaints/{id}/attachments   any
POST   /api/v1/complaints/{id}/attachments   any        multipart/form-data, single file
GET    /api/v1/complaints/{id}/attachments/{aid}  any   302 → short-lived read SAS
GET    /api/v1/complaints/{id}/escalations   Staff/DeptAdmin

── SLA, dashboard, ops ──────────────────────────────────────────────────────────
GET    /api/v1/dashboard/summary             any        role-scoped, see F13
GET    /api/v1/sla/breaches                  DeptAdmin  own department
GET    /api/v1/complaints/export             DeptAdmin  CSV, honours the list filters
POST   /api/v1/admin/sla/sweep               DeptAdmin  runs one sweep, returns its counters
GET    /health                               anon       liveness
GET    /health/ready                         anon       DB + blob reachability
```

**One `transitions` endpoint, not seven verb endpoints** (D3, §23). Every movement passes the guard
table; no route can bypass it.

### 13.3 The list endpoint

`GET /api/v1/complaints` — the query surface every screen is built on.

| Parameter | Values |
|---|---|
| `status` | repeatable; any of the six |
| `categoryId` | Guid |
| `departmentId` | Guid — accepted but **ignored unless it equals the caller's own department**; it can never widen scope |
| `assignedToMe` | bool — `Staff` / `DeptAdmin` only |
| `unassigned` | bool — `DeptAdmin` |
| `slaState` | `ok` \| `warning` \| `breached` |
| `priority` | repeatable |
| `q` | free text over `Title`, `Description`, `ReferenceNumber`, `AddressText`, matched with `ILIKE` (§8.12) |
| `from`, `to` | `CreatedAt` bounds |
| `sort` | `createdAt` \| `slaDueAt` \| `priority`, `-` prefix for descending; default `-createdAt` |
| `page`, `pageSize` | 1-based, default 20, max 100 |

`slaState` semantics — stated explicitly because they are not symmetric:

- `ok` — open **and** `now < CreatedAt + 0.8 × window`
- `warning` — open **and** `0.8 × window ≤ now − CreatedAt < window`
- `breached` — `SlaBreachedAt IS NOT NULL`, **regardless of current status**. A breach is historical
  and survives resolution: a resolved-late complaint still answers this filter.

Every complaint DTO returned by the list and the detail endpoints carries
**`availableActions: string[]`** — the actions this caller may perform on this complaint right now,
computed from the guard table. The UI renders buttons from it (§15).

---

## 14. Feature specifications

Each feature has acceptance criteria a test or a demonstrable UI path must satisfy. The milestone
that delivers it is in the heading; §20 is the live tracker.

### F1 — Solution skeleton & CI gate · M1

- `Obhijog.sln` with the four projects of §6 and the one-way reference direction.
- `Directory.Build.props` sets `net9.0`, `Nullable=enable`, `ImplicitUsings=enable`,
  `TreatWarningsAsErrors=true`, `LangVersion=latest`.
- `web/` is an Angular 20 workspace with Material and a routed shell that builds clean.
- `infra/docker-compose.yml` brings up PostgreSQL 17 and Azurite.
- `GET /health` returns 200. `GET /health/ready` checks the database and the blob container.
- `.github/workflows/ci.yml` runs the full gate (§18) on pull requests and is green.

### F2 — Domain model, migration & seed · M2

- Every entity of §8 with its constraints and the five `Complaint` indexes.
- `ComplaintReferenceSeq` created by migration.
- `dotnet ef database update` succeeds against an empty database.
- The seeder is idempotent: running it twice leaves identical row counts.
- Seeding without `SEED_PASSWORD` fails with a clear message and writes nothing.

### F3 — Authentication & roles · M3

- Register / login / refresh / logout / me per §10.
- Refresh rotation: reusing a revoked token returns `401` **and** revokes the family.
- Startup fails when `Jwt:SigningKey` is missing or shorter than 32 bytes.
- A `Staff` token on a `DeptAdmin` endpoint → `403`; no token → `401`.
- Angular: login and register screens, token storage, auth interceptor, refresh-on-`401` with a
  single in-flight refresh, functional route guards, and role-based landing — `/my` for Citizen,
  `/queue` for Staff, `/inbox` for DeptAdmin.

### F4 — Complaint submission · M4

- `POST /complaints` validates title, description, coordinate ranges and an **active** category.
- Sets `DepartmentId` and `Priority` from the category, allocates a reference number from the
  sequence, computes `SlaDueAt`, writes the initial `New` history row, and queues a
  `ComplaintSubmitted` notification.
- Returns `201` with `Location` and the created complaint including its reference number.
- An inactive category → `400`. A `Staff` or `DeptAdmin` caller → `403`; only Citizens submit.

### F5 — Citizen views & public tracking · M4

- `GET /complaints` for a Citizen returns only their own; another citizen's id in `GET /{id}`
  → `404`.
- Citizen list and detail screens: status chip, SLA countdown, timeline from `history`, comments.
- `by-reference` works unauthenticated and returns exactly the redacted projection of §9.5 — a test
  asserts the response body contains no citizen or staff identity.

### F6 — Photo attachments · M5

- `POST /attachments` accepts one `multipart/form-data` file; validates content type against
  `Attachments:AllowedContentTypes`, size against `Attachments:MaxSizeBytes` (5 MB), and count
  against `Attachments:MaxPerComplaint` (5).
- Wrong type → `415`; too large → `413`; over the count → `400`.
- Blob name is `{complaintId}/{attachmentId}{ext}`; the user filename is stored but never used as a
  path component.
- `GET /attachments/{aid}` returns `302` to a read-only SAS valid for `Storage:ReadSasMinutes`
  (15). The container is private — a direct container URL without the SAS fails.
- Angular: upload with progress, and a thumbnail gallery on the detail screen.

### F7 — State machine & transitions · M6

- The twelve rows of §12.3 implemented as one table. A unit test walks **every** row and asserts a
  representative sample of absent `(from, action)` pairs is rejected with `409`.
- Missing required payload → `400` naming the field.
- Wrong role → `403`; assignee-only violation by a department colleague → `403`; out-of-scope
  complaint → `404`.
- An `assigneeId` that is inactive, not `Staff`, or in another department → `400`.
- Each accepted transition writes one history row in the same transaction; a stale `xmin`
  → `409`.
- The response includes the complaint's new `availableActions`.

### F8 — Staff queue & department inbox · M6

- Staff: "my queue" filtered to `assignedToMe`, with `start` and `resolve` actions.
- DeptAdmin: department inbox with the filters of §13.3, plus `assign`, `reassign`, `recategorize`,
  `reject`, `close`, and the priority edit.
- **Action buttons render from the `availableActions` the API returned**, never from a client-side
  copy of the guard table.
- Every hidden control still has a server-side guard, and F7's tests prove it.

### F9 — Comments & history timeline · M6

- Chronological comment list; the `isInternal` toggle is visible only to Staff / DeptAdmin.
- A Citizen never receives an internal comment — asserted by test at the query level.
- The timeline merges history rows and escalation events into one chronological view.

### F10 — SLA computation & display · M7

- `SlaDueAt` stored at creation and recomputed only per §11.4.
- A shared SLA badge component renders `ok` / `warning` / `breached` with a live countdown, driven
  by fields the API returns (`slaDueAt`, `slaBreachedAt`, `escalationLevel`). **The client does not
  re-derive thresholds.**

### F11 — The sweeper · M7

- `ISlaSweeper.SweepAsync` implements the ladder of §11.2 with the idempotency of §11.3.
- `SlaSweepService : BackgroundService` calls it every `Sla:SweepIntervalSeconds`;
  `0` disables the hosted service.
- `POST /admin/sla/sweep` calls the **same** method and returns
  `{ examined, warned, breached, escalatedLevel2, autoClosed, durationMs }`.
- Tests, via an injected `TimeProvider`: warn at 80%, breach at 100%, level 2 at 150%,
  **two sweeps produce one escalation per level**, both markers set in one pass when a complaint
  crossed both, level-2 complaints never re-selected, resolved complaints never selected,
  auto-close at 7 days.
- Hitting the batch cap logs a visible line.

### F12 — Escalation & notifications · M7

- Every ladder step writes its `EscalationEvent` and `Notification` rows inside the transition's
  transaction.
- Recipients per §11.2. Level 1 notifies the assignee **and** every active Dept Admin.
- `INotificationSender`'s MVP implementation writes a structured log line and stamps `SentAt`; a
  failure increments `Attempts` and records `LastError` **without failing the sweep**.
- `GET /sla/breaches` lists breached open complaints in the caller's department with escalation
  level and hours overdue.
- M7 seed adds the three overdue complaints so the ladder is demonstrable immediately.

### F13 — Dashboard · M8

`GET /dashboard/summary`, role-scoped through `ComplaintQueryScope`:

| Field | Definition |
|---|---|
| `totalOpen` | status ∈ New, Assigned, InProgress |
| `byStatus` | count per status, all six |
| `warningOpen` | open and inside the 80–100% band |
| `breachedOpen` | open and `SlaBreachedAt IS NOT NULL` |
| `dueNext24h` | open and `SlaDueAt` within 24 hours |
| `escalatedLevel1`, `escalatedLevel2` | open, by level |
| `resolvedLast30Days` | `ResolvedAt` within 30 days |
| `avgResolutionHours` | mean `ResolvedAt − CreatedAt` over those |
| `slaCompliancePct` | of those, the share with `ResolvedAt <= SlaDueAt` |

- A Citizen sees the same shape computed over their own complaints only.
- Acceptance: every figure matches a hand count over the seed data.

### F14 — CSV export · M8

- `GET /complaints/export` applies the **same** filter and scope code as the list endpoint. A
  divergence between the two is a bug, not a feature.
- `text/csv` with a `Content-Disposition` filename. Columns: reference, title, category, department,
  status, priority, assignee, created, due, resolved, breached, escalation level.
- Fields are quoted, and a leading `=`, `+`, `-` or `@` is prefixed with `'` so a spreadsheet does
  not execute it.

### F15 — Infrastructure as code & deploy workflow · M9

- `infra/main.bicep`: an **Azure Database for PostgreSQL flexible server** (Burstable `B1ms`, the
  cheapest tier that exists — this is a portfolio project) plus its database, a Storage account with
  the private container, a Container Apps environment and app for the API, a Static Web App for
  `web/`, Key Vault, and a user-assigned managed identity carrying the Blob role assignment.
- **Firewall:** no `0.0.0.0` rule. The Container App reaches the database over VNet integration, or
  failing that through `allowAzureServices` — never a public allow-all, not even briefly.
- **No secret literals.** The API reads `ConnectionStrings:Postgres`, `Jwt:SigningKey` and
  `Storage:ConnectionString` from Key Vault references; Blob access prefers managed identity.
- Database auth uses a password in Key Vault. Entra-managed-identity auth to PostgreSQL is
  possible and is the better end state, but it needs a token-refreshing connection provider — it is
  named here as the follow-up rather than half-built (see D10).
- `Dockerfile` for the API: multi-stage, non-root, builds.
- `.github/workflows/deploy.yml` is **`workflow_dispatch` only** — it never fires on push.
- Acceptance: `az deployment group what-if` reports no errors. Actually deploying is optional.

### F16 — Async escalation via Service Bus + Functions · M10 · **stretch**

- Detection stays where it is; only **delivery** moves. On breach, the sweeper publishes an
  `SlaBreached` message instead of writing notifications inline, when `Sla:Transport = ServiceBus`.
- `functions/Obhijog.Functions` — an isolated-worker Service Bus trigger that writes the
  notification rows, sharing the `Infrastructure` project.
- Because handling is idempotent (§11.3), a redelivered message is harmless. That is the whole point
  of the design, and a test asserts it.
- `Sla:Transport = InProcess` remains the default and must keep working.

---

## 15. Frontend architecture

`web/` — Angular 20, standalone components only, no NgModules.

```
src/app/
  core/            auth service, token store, interceptors, CurrentUser signal, guards,
                   API models (one file, hand-mirrored from the API DTOs)
  shared/          sla-badge, status-chip, priority-chip, complaint-timeline,
                   attachment-gallery, map-picker, confirm-dialog
  features/
    auth/          login, register
    citizen/       submit, my-complaints, complaint-detail
    public/        track-by-reference
    staff/         my-queue
    admin/         inbox, dashboard, categories, staff-management
  app.routes.ts    lazy loadComponent per feature, guarded by role
```

Conventions:

- **State is signals.** `signal`, `computed`, and `resource` / `httpResource` for reads. No NgRx and
  no RxJS `BehaviorSubject` stores. `HttpClient` is fine; a bare `.subscribe()` in a component is
  not — use `resource` or `toSignal`.
- **Guards are functional** (`CanActivateFn`): one `authGuard` plus one per role.
- **One interceptor chain:** attach bearer → refresh once on `401` (single in-flight refresh, queued
  retries) → surface the `ProblemDetails` `title` / `detail` in a snackbar.
- **The client never re-derives authorization or SLA thresholds.** The complaint DTO carries
  `availableActions` and the SLA fields; buttons and badges render from those. A client-side copy of
  the guard table is a review finding.
- **Angular Material** for table, paginator, dialog, snackbar, chips and form fields. No Tailwind.
- Leaflet lives in one `map-picker` component: browser geolocation for "use my location", a
  draggable pin, emitting `{ lat, lng }`; a read-only mode for the detail screen.
- `ng build` must be warning-clean — it is the frontend gate (§18).

---

## 16. Backend architecture

### 16.1 Endpoint groups

Minimal APIs, one file per feature under `src/Obhijog.Api/Endpoints/`: `AuthEndpoints`,
`ReferenceEndpoints`, `UserEndpoints`, `ComplaintEndpoints`, `TransitionEndpoints`,
`CommentEndpoints`, `AttachmentEndpoints`, `DashboardEndpoints`, `SlaEndpoints`, `HealthEndpoints`.
Each exposes `static RouteGroupBuilder Map<X>(this IEndpointRouteBuilder)`, with one
exception: `HealthEndpoints` maps `/health` and `/health/ready` at the root rather than under
`/api/v1` — an orchestrator probe should not have to be reconfigured the day the API version
changes — so it is not a route group and returns `IEndpointRouteBuilder`.

An endpoint does three things only: bind and validate the request, call one service, map the result
to a status code. **No EF query lives in an endpoint file.**

### 16.2 Services

Business logic lives in `Infrastructure` services over `Domain` types:

| Service | Responsibility |
|---|---|
| `ComplaintService` | create, read, list (through `ComplaintQueryScope`), export projection |
| `ComplaintTransitionService` | the single write path for status changes: consults `ComplaintStateMachine`, writes history, applies clock effects, raises notifications |
| `AttachmentService` | validation, `IAttachmentStore`, SAS issuance |
| `SlaSweeper : ISlaSweeper` | the ladder and auto-close |
| `NotificationService` | writes `Notification` rows; delegates delivery to `INotificationSender` |
| `DashboardService` | the F13 aggregates |
| `TokenService` | JWT issuance, refresh rotation, family revocation |

### 16.3 Domain layer

`Obhijog.Domain` holds the entities, the enums, `ComplaintStateMachine` (the guard table plus
`TryTransition`), and `SlaPolicy` (the threshold arithmetic — `DueAt`, `WarnAt`, `Level2At`,
`ElapsedPercent`). Both are pure functions over values and an injected `TimeProvider`, which is why
they are the cheapest and most valuable things in the codebase to test.

### 16.4 Cross-cutting

- **`ProblemDetails` everywhere.** One `IExceptionHandler` maps `NotFoundException → 404`,
  `ForbiddenException → 403`, `InvalidTransitionException → 409`,
  `DbUpdateConcurrencyException → 409`, `ValidationException → 400`. No endpoint returns
  `BadRequest` from its own `try/catch`.
- **`TimeProvider` injected.** No direct `DateTimeOffset.UtcNow` outside `Program.cs`.
- **Structured logging** via `ILogger<T>` with a `traceId` scope; that id is echoed in every
  `ProblemDetails`.
- **Migrations are forward-only** once committed. A bad migration is fixed by writing another one —
  never by editing or deleting a file under `Infrastructure/Migrations/`.
- **Read paths are no-tracking projections to DTOs**, never entity graphs.

---

## 17. Non-functional requirements

| Requirement | Target | How it is met |
|---|---|---|
| List query latency | < 300 ms at 50k complaints | the five indexes of §8.4; projections, not entity graphs |
| Sweep duration | < 2 s per pass at 50k complaints | the partial `(Status, SlaDueAt)` index; batch cap of 200 |
| Attachment limits | 5 MB, 5 per complaint | validated server-side → `413` / `400` |
| Token lifetime | access 15 min, refresh 14 days, rotating | §10.2 |
| Passwords | Identity defaults, ≥ 10 chars, no dev password in source | §8.11 |
| Blob privacy | container private; reads only via a 15-minute SAS | F6 |
| Secrets | none in source or committed config; Key Vault in Azure, env vars locally | §19 |
| Concurrency | lost updates impossible on `Complaint` | `xmin` → `409` |
| Audit | every status change attributable | append-only `ComplaintStatusHistory` |
| Accessibility | keyboard-navigable, labelled fields, ≥ 4.5:1 contrast | Material defaults; not separately tested |

Explicitly **not** required: horizontal scale-out of the sweeper — one instance owns it, and if the
API is scaled out then `Sla:SweepIntervalSeconds = 0` disables the hosted service on all but one
replica. That is documented rather than solved with a distributed lock. Also not required: rate
limiting, CAPTCHA, soft delete, GDPR erasure.

---

## 18. Testing strategy

**A deliberately small bar.** Four suites, chosen because each covers logic that is easy to get
wrong and expensive to get wrong. Do not add coverage beyond this out of habit; a pull request that
adds a fifth suite needs a reason in its description.

| Suite | File | Covers |
|---|---|---|
| State machine | `tests/Obhijog.Tests/ComplaintStateMachineTests.cs` | all twelve rows of §12.3 allowed; a sample of absent pairs rejected; role and assignee-only guards; required-payload validation |
| SLA arithmetic | `tests/Obhijog.Tests/SlaPolicyTests.cs` | `DueAt`, `WarnAt`, `Level2At`, `ElapsedPercent`; the recategorize recompute; the reopen reset — all through a fake `TimeProvider` |
| Sweep idempotency | `tests/Obhijog.Tests/SlaSweeperTests.cs` | the ladder; **two sweeps → one escalation per level**; both markers in one pass; level 2 never re-selected; resolved never selected; auto-close at 7 days |
| Role scoping | `tests/Obhijog.Tests/ComplaintScopeTests.cs` | a Citizen cannot read another citizen's complaint (**404**, not 403); Staff cannot read another department's; Staff cannot act on a colleague's assignment (**403**); internal comments absent from a Citizen's query |

- Sweep and scope tests run against **EF Core on a real PostgreSQL** where one is reachable, and are
  **skipped with a visible message** otherwise — never silently reported as passing. They must not
  use the EF in-memory provider: it has no transactions, no `xmin`, no unique-index enforcement and
  no `ILIKE`, so it would silently pass the exact cases §11.3 exists to guarantee.
- **Frontend: zero tests.** `ng build` is the gate. This is decision D6, not an omission.
- No test spins up Azurite; `IAttachmentStore` is doubled.

**The four-suite bar counts application tests.** One suite sits outside it:
`.claude/hooks/__tests__/run-hook-tests.mjs` covers the guard hooks and runs in the `harness` CI
job. It is not an exception being smuggled in — the guards are the only thing enforcing the working
agreement across a long unattended run, and their failure mode is silent: a guard that stops
working simply allows everything and nothing looks wrong. That is exactly what happened once (a
byte-order mark on the payload made `JSON.parse` throw, the payload became `{}`, and every guard
allowed every command), which is why the suite exists and why `readPayload` now fails **closed** on
input it cannot parse.

### The gate

Every pull request must pass, in this order:

```
dotnet format --verify-no-changes
dotnet build --configuration Release      # TreatWarningsAsErrors=true
dotnet test  --configuration Release
npm ci      --prefix web
npm run build --prefix web
```

CI runs it against a real PostgreSQL service container on the exact SHA that will merge. The same
commands run locally once the .NET 9 SDK is installed (§5).

---

## 19. Configuration & environments

No secret ever lands in a committed file. `appsettings.json` carries structure and safe defaults;
values come from environment variables locally and Key Vault references in Azure.

**One deliberate exception**, and only one: `.github/workflows/ci.yml` defines the database
password and JWT signing key for the PostgreSQL and Azurite containers it creates and destroys
inside a single job. Those containers are unreachable from outside the job and the values exist
nowhere else, so they are not secrets. They are written as plain literals rather than as
`secrets.X || 'literal'` fallbacks, because a fallback would disguise the literal while changing
nothing. No deployed environment ever reads them, and no other file may follow this pattern.

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:Postgres` | — | **required**, no default. `Host=…;Port=5432;Database=obhijog;Username=…;Password=…;SSL Mode=Disable` locally; `SSL Mode=Require;Trust Server Certificate=false` in Azure |
| `Jwt:Issuer` / `Jwt:Audience` | `obhijog` | |
| `Jwt:SigningKey` | — | **required, ≥ 32 bytes; startup fails otherwise** |
| `Jwt:AccessTokenMinutes` | `15` | |
| `Jwt:RefreshTokenDays` | `14` | |
| `Storage:ConnectionString` | — | required; the Azurite string locally, managed identity in Azure |
| `Storage:Container` | `complaint-attachments` | private |
| `Storage:ReadSasMinutes` | `15` | |
| `Attachments:MaxSizeBytes` | `5242880` | |
| `Attachments:AllowedContentTypes` | `image/jpeg,image/png,image/webp` | |
| `Attachments:MaxPerComplaint` | `5` | |
| `Sla:SweepIntervalSeconds` | `60` | `0` disables the hosted service |
| `Sla:WarningThresholdPercent` | `80` | |
| `Sla:EscalationLevel2Percent` | `150` | |
| `Sla:AutoCloseAfterDays` | `7` | |
| `Sla:SweepBatchSize` | `200` | |
| `Sla:Transport` | `InProcess` | `ServiceBus` from M10 |
| `ServiceBus:ConnectionString` / `:QueueName` | — / `sla-events` | M10 only |
| `Notifications:Delivery` | `Log` | `Email` is a later swap |
| `Cors:Origins` | `http://localhost:4200` | |
| `SEED_PASSWORD` | — | environment variable only; seeding fails without it |
| `POSTGRES_PORT` | `5432` | `infra/docker-compose.yml` only — the published host port. Override it when something already owns 5432; a native PostgreSQL service shadows the container and fails authentication against the wrong server. `ConnectionStrings:Postgres` must use the same value. |

### Ports — these four change together

| Thing | Value |
|---|---|
| API | `http://localhost:5080`, base path `/api/v1` |
| Angular dev server | `http://localhost:4200` |
| `Cors:Origins` | must contain the Angular origin |
| `web/src/environments/environment.ts` → `apiBaseUrl` | must be `http://localhost:5080/api/v1` |

Changing the API port means changing the Angular environment file **and** `Cors:Origins` **and**
`infra/docker-compose.yml` **in the same commit**. A mismatch here 404s or CORS-fails every request
at the network layer and looks convincingly like an application bug.

`.env.example` is committed and lists every variable with placeholder values. A new variable lands in
`.env.example`, `appsettings.json`, this table and the Bicep parameters in one commit.

---

## 20. Delivery roadmap

**This table is the live progress tracker.** `☐` not started · `◐` in progress · `☑` done with its
Definition of Done actually passing. Update it in the milestone's own pull request.

| M | Issue | Milestone | Features | Depends on | Definition of done | State |
|---|---|---|---|---|---|---|
| M1 | #2 | Solution skeleton, CI, health | F1 | — | Four projects build with `-warnaserror`; the Angular shell builds; `docker compose up` gives PostgreSQL 17 + Azurite; `/health` and `/health/ready` return 200; the CI gate is green on the PR | ☑ |
| M2 | #3 | Domain model, migration, seed | F2 | M1 | `dotnet ef database update` from empty succeeds; every §8 constraint and index present; the seeder is idempotent; seeding without `SEED_PASSWORD` fails cleanly | ☑ |
| M3 | #4 | Authentication & roles | F3 | M2 | Three roles log in and land on their own route; refresh rotation revokes families; a missing signing key fails startup; `Staff` → `403` on a `DeptAdmin` endpoint | ☐ |
| M4 | #5 | Submission, citizen views, public tracking | F4, F5 | M3 | A Citizen submits and sees a reference number and SLA countdown; another citizen's complaint → `404`; `by-reference` works anonymously and leaks no identity | ☐ |
| M5 | #6 | Photo attachments via Blob | F6 | M4 | A photo round-trips through Azurite; `415` / `413` / count limits enforced; reads only via SAS; the gallery renders | ☐ |
| M6 | #7 | State machine, transitions, history, comments | F7, F8, F9 | M4 | `New→Assigned→InProgress→Resolved→Closed` plus `reject` and `reopen`, all driven from the UI; every guard-table row tested; internal comments invisible to Citizens | ☐ |
| M7 | #8 | SLA engine: warning, breach, escalation, notifications | F10, F11, F12 | M6 | A seeded overdue complaint escalates L1 then L2; **a second sweep changes nothing**; badges and the breach list render; the manual sweep endpoint returns counters | ☐ |
| M8 | #9 | Dashboard & CSV export | F13, F14 | M7 | Every dashboard figure matches a hand count on seed data; the export shares the list's filter and scope code; CSV injection neutralised | ☐ |
| M9 | #10 | Bicep & deploy workflow | F15 | M8 | `az deployment group what-if` is clean; the API image builds; the deploy workflow is dispatch-only; no secret literals | ☐ |
| M10 | #11 | **Stretch** — Service Bus + Functions escalation | F16 | M7 | A breach publishes to Service Bus; the Function writes the notifications; a redelivered message is provably harmless; `InProcess` still works | ☐ |

M1 through M6 are a straight chain. M5 and M6 both depend only on M4 and are independent of each
other. M9 needs M8; M10 needs M7. M9 and M10 are independent of each other.

---

## 21. Working agreement

1. **`SPEC.md` is authoritative.** Code that disagrees with it means one of the two is wrong — fix
   both in the same pull request.
2. **One issue → one branch → one pull request → one squash merge.** Branch `feat/<n>-<slug>` from
   fresh `main`. `main` is hook-protected; committing on it is blocked.
3. **Vertical slices.** A milestone ships migration → service → endpoint → Angular service → UI →
   tests. Never "all the backend, then all the frontend".
4. **Propagate in the same commit.** Anything that exists twice moves together: entity ↔ migration ↔
   seed ↔ DTO ↔ Angular model ↔ this file. A new config key lands in `appsettings.json`,
   `.env.example`, §19 and the Bicep parameters at once. Stale docs are bugs.
5. **Backend enforcement is authoritative.** The UI hides what a user cannot do purely for looks;
   every hidden control still has a server-side guard and a test proving it.
6. **Verify, don't assert.** Work ends with the gate of §18 actually executed and its output shown.
   A failure is reported, not glossed over. "Not verified" is an acceptable report; "passing" when
   it was never run is not.
7. **Merge blockers are exactly four:** red CI · a test that cannot fail · a violation of a §9
   invariant · a HIGH security finding. Everything else is advisory and becomes a follow-up issue.
8. **Record unknowns, don't guess.** Add a row to §23 or open an issue. Decisions that are costly to
   reverse get an ADR in `docs/adr/`.
9. **Commit only when asked**, with a Conventional Commit subject.
10. **Never force-push, never `--no-verify`, never edit a committed migration.**

---

## 22. Out of scope

Named so nobody adds them by reflex: multiple municipalities or tenancy · business-hours and holiday
SLA calendars · SLA pause / `OnHold` · SignalR or any real-time push · real email or SMS delivery in
the MVP · duplicate and near-duplicate detection · geospatial clustering or heat-maps · citizen
satisfaction surveys · a super-admin role · department CRUD · soft delete and GDPR erasure · rate
limiting and CAPTCHA · i18n and localisation · a mobile app · offline submission · payments of any
kind · distributed locking for a scaled-out sweeper · PDF reporting · a public statistics portal.

---

## 23. Decision log

| # | Decision | Rejected alternative | Why |
|---|---|---|---|
| D1 | Trim the MVP to Azure Database for PostgreSQL + Blob; escalation runs in-process, with Service Bus + Functions as M10 | Five Azure services from day one | Five services in an MVP is where a portfolio project stops being finished. The sweeper's seam keeps the async story credible and one flag away. |
| D2 | Own JWT + ASP.NET Core Identity | Microsoft Entra External ID (B2C) | Entra costs a milestone of portal configuration, is painful to run locally, and blocks every downstream milestone. Roles-as-claims demonstrates the same skill. |
| D3 | One `POST /complaints/{id}/transitions` endpoint | Seven verb endpoints (`/assign`, `/resolve`, …) | Less code, and no route can bypass the guard table. The action name becomes data, so the UI renders buttons from `availableActions`. |
| D4 | A calendar-hour SLA clock that never pauses; no `OnHold` status | A business-hours calendar with pause/resume accounting | A holiday calendar and accumulated-pause arithmetic is a project of its own. Stated as a simplification rather than half-built. |
| D5 | `recategorize` always lands in `New` and clears the assignee | A conditional target depending on whether the department changed | Keeps the guard table free of conditionals — one row per `(from, action)`. It slightly over-resets when the department is unchanged; acceptable. |
| D6 | Zero frontend tests; `ng build` is the frontend gate | Karma or Jest component tests | An explicit choice for a small bar. The logic worth testing was deliberately pushed down into `Domain`, which *is* tested. |
| D7 | Attachments stream through the API | Direct-to-blob upload with a write SAS | Direct upload needs storage CORS and a two-step register flow. Streaming works identically on Azurite. Recorded as the natural first improvement. |
| D8 | A global, never-reset reference sequence | A per-year counter that resets | No read-modify-write race and no cross-year collision. The year in the string is informational. |
| D9 | `404` for out-of-scope complaints; `403` only for visible-but-forbidden actions | `403` throughout | A `403` confirms the row exists. §9.2 makes this an invariant and a merge blocker. |
| D10 | **PostgreSQL** (Azure Database for PostgreSQL flexible server), not Azure SQL | Azure SQL / SQL Server | Decided before any migration was written, so the cost was documentation only. PostgreSQL brings a cheaper Burstable tier, a much faster CI service container, and portability off Azure. The costs are real and are accepted: no `rowversion` (§8.12 uses `xmin`), no `tinyint`, case-sensitive comparison (`ILIKE` in §13.3), and Entra-identity database auth needing a token-refreshing provider — so M9 uses a Key Vault password and names identity auth as the follow-up. |
| D11 | `snake_case` naming, applied globally by `EFCore.NamingConventions` | EF's default PascalCase columns, or hand-written `HasColumnName` | PascalCase in PostgreSQL means every identifier needs double quotes in any hand-written SQL, which is a permanent tax on migrations, `psql` and index filters. One line of configuration beats a `HasColumnName` on every property. The catch is documented in §8.12: `HasFilter` takes raw SQL and is **not** rewritten by the convention, so partial-index filters must be written in snake_case by hand. |
| D13 | `User` and `Role` live in `Obhijog.Infrastructure`, not `Obhijog.Domain` | Putting every §8 entity in Domain, as §6 implies | §8.1 requires `User : IdentityUser<Guid>`, and that base type comes from the ASP.NET Identity stack. Placing it in Domain would mean a package reference the layer is forbidden to carry (§16.3, CLAUDE.md non-negotiable 7, `backend-dotnet.md`) — one of the two rules had to give, and the purity rule is the one with tests, review checks and a documented reason behind it. The cost is that Domain entities hold `CitizenId`, `AssignedStaffId`, `AuthorId`, `RecipientId` and `ChangedById` as bare `Guid`s with no navigation property. That is a real loss of expressiveness in exchange for a layer that stays testable without a database. |
| D12 | Project named **Obhijog** (অভিযোগ, Bangla for "complaint"); transliteration fixed as `Obhijog` | Keeping `MunicipalSla`, or `Nagorik` / `NagorikSeba` / `Prohori` | Decided before anything was scaffolded, so the cost was text only — no namespace, migration or package rename to pay for later. `Obhijog` names the domain's central entity rather than its actor or its watchdog. The spelling is pinned here because Bangla transliteration is unstable: `Ovijog` and `Abhijog` are equally defensible and neither is used anywhere in this repo. The PostgreSQL database and role, and `Jwt:Issuer` / `Jwt:Audience`, are all plain `obhijog`. |
