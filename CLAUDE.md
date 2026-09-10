# Obhijog — Municipal Complaint & SLA Tracking · agent working rules

**অভিযোগ** — *obhijog*, Bangla for "complaint". A civic complaint desk with SLA tracking and
automatic escalation.
**.NET 9 Minimal APIs + EF Core 9 + Azure Database for PostgreSQL** API, **Angular 20 standalone + signals + Material**
web app, **Azure Blob Storage** for photos. Three roles: `Citizen`, `Staff`, `DeptAdmin`.

## Source of truth

[SPEC.md](SPEC.md) is authoritative for *what* gets built and *in what order*. It is not background
reading — it is the requirements document. **Code that disagrees with SPEC.md is a bug in one of the
two; fix both, in the same pull request.**

Sections you will need constantly:

| Need | Section |
|---|---|
| Data model, columns, indexes | §8 |
| **Authorization & scoping invariants** | **§9** |
| Auth flows, token lifetimes | §10 |
| **SLA ladder & the idempotency rules** | **§11** |
| **The state-machine guard table** | **§12.3** |
| API conventions, the full surface, list filters | §13 |
| Per-feature acceptance criteria (`F1`–`F16`) | §14 |
| Frontend / backend architecture | §15, §16 |
| Testing bar — and what is *not* required | §18 |
| Config keys and the four things that share a port | §19 |
| **Milestone roadmap + Definition of Done** | **§20** |
| Working agreement | §21 |
| Why something is the way it is | §23 |

## Where we are

**M1 is done; M2 (issue #3) is next.** The solution, the four projects, the Angular workspace and
`infra/docker-compose.yml` all exist. There is no data model yet — `ObhijogDbContext` holds no
entities and no migration has been written, so `/health/ready` checks a reachable but empty
database. The roadmap table in §20 is the live progress tracker: `☐` not started, `◐` in progress,
`☑` done with its DoD actually passing. Read it before starting anything; tick the box in the
milestone's own PR, only once the DoD really passes.

## Non-negotiables

These are the rules an agent is most likely to violate. Everything else is in `.claude/rules/`.

1. **Out of scope returns `404`, never `403`.** A complaint the caller may not read must be
   indistinguishable from one that does not exist. `403` is correct *only* when the caller can see
   the complaint but may not perform the action. SPEC §9.2. **This is a merge blocker.**

2. **One scoping seam.** Every complaint query is built from
   `ComplaintQueryScope.For(source, currentUser)`. A hand-rolled `Where(c => c.CitizenId == …)` in
   an endpoint is a review finding *even when it is correct*, because the next one will not be.
   SPEC §9.3.

3. **Nothing moves a complaint except the guard table.** All twelve transitions go through
   `ComplaintStateMachine` in `Obhijog.Domain`, via `ComplaintTransitionService`, via the
   single `POST /complaints/{id}/transitions` endpoint. No verb endpoints, no direct
   `complaint.Status = …` anywhere else. SPEC §12.

4. **The sweep is idempotent or it is broken.** Running it twice must produce one escalation per
   level and one notification per recipient. Three mechanisms hold that up — the not-yet-done
   `WHERE` clause, one transaction per complaint, and the unique index on
   `(ComplaintId, ReopenCount, Level)`. Never remove one of them "because the others cover it".
   SPEC §11.3.

5. **`TimeProvider` is injected.** No `DateTimeOffset.UtcNow` outside `Program.cs`. The SLA tests
   cannot exist without this, so a direct clock call fails review.

6. **The client re-derives nothing.** The complaint DTO carries `availableActions` and the SLA
   fields; Angular renders buttons and badges from them. A client-side copy of the guard table or
   of the 80/100/150 thresholds is a review finding. SPEC §15.

7. **Dependency direction is one-way:** `Api → Infrastructure → Domain`. `Obhijog.Domain`
   takes no package reference to EF Core or ASP.NET. Business logic lives in Infrastructure
   services over Domain types; endpoints bind, call one service, and map a status code.

8. **Migrations are forward-only** once committed. Fix a bad migration by writing another one;
   never edit or delete a file under `src/Obhijog.Infrastructure/Migrations/`.

9. **Propagate in the same commit.** Anything that exists twice moves together: entity ↔ migration ↔
   seed ↔ DTO ↔ Angular model ↔ SPEC.md. A new config key lands in `appsettings.json`,
   `.env.example`, SPEC §19 and the Bicep parameters at once. Stale docs are bugs.

10. **Never widen the port/CORS set unilaterally.** API `:5080/api/v1`, web `:4200`,
    `Cors:Origins`, and `web/src/environments/environment.ts` change **together**. A mismatch here
    404s or CORS-fails every request at the network layer and looks exactly like an application bug.
    SPEC §19.

11. **No secrets in source.** No connection string, signing key or seed password as a literal —
    not in `appsettings.json`, not in a test, not in a compose file. `SEED_PASSWORD` comes from the
    environment and seeding **fails** without it rather than planting a known password.

12. **Verify, don't assert.** Work ends with the gate below actually executed and its output shown.
    Report a failure as a failure. "Not verified" is an acceptable report; "passing" when it was
    never run is not.

13. **Commit only when asked**, with a Conventional Commit subject. `main` is hook-protected —
    branch first, merge through a PR.

## Testing bar — and its ceiling

Four xUnit suites, listed in SPEC §18: the state machine, the SLA arithmetic, sweep idempotency,
and role scoping. **Frontend tests: zero** — `ng build` is the frontend gate, and that is decision
D6, not an oversight.

Do not add coverage beyond those four out of habit. A PR that adds a fifth suite needs a reason in
its description.

## Commands

```bash
# --- API (repo root) ---
dotnet restore
dotnet build   --configuration Release        # TreatWarningsAsErrors=true
dotnet test    --configuration Release
dotnet format  --verify-no-changes            # run before every commit
dotnet run     --project src/Obhijog.Api      # http://localhost:5080

# --- EF Core (migrations live in Infrastructure, startup is Api) ---
dotnet ef migrations add <Name> \
  --project src/Obhijog.Infrastructure --startup-project src/Obhijog.Api
dotnet ef database update \
  --project src/Obhijog.Infrastructure --startup-project src/Obhijog.Api

# --- web ---
npm ci        --prefix web
npm start     --prefix web                    # ng serve :4200
npm run build --prefix web                    # the frontend gate

# --- local infrastructure ---
docker compose -f infra/docker-compose.yml up -d     # PostgreSQL 17 + Azurite
docker compose -f infra/docker-compose.yml ps
```

### The gate

```
dotnet format --verify-no-changes && dotnet build -c Release && dotnet test -c Release \
  && npm ci --prefix web && npm run build --prefix web
```

CI runs exactly this against a real PostgreSQL service container on the SHA that will merge.
Prefer reading `gh pr checks` over rebuilding it locally.

## Environment notes

- **The .NET SDK is installed but not on `PATH`.** It lives at `C:Program Filesdotnet`
  (10.0.401). Prepend it — `export PATH="/c/Program Files/dotnet:$PATH"` in Git Bash — and the
  full gate runs locally. Only the .NET 10 runtime is present, so `dotnet run` and `dotnet test`
  need `DOTNET_ROLL_FORWARD=Major`; `build` and `format` do not. CI pins 9.0.x, so it remains
  the authority: a local pass is evidence, not a verdict.
- **Port 5432 is occupied** by a native PostgreSQL service, which shadows the compose container.
  Set `POSTGRES_PORT` in `infra/.env` and match it in the connection string, or authentication
  fails against the wrong server.
- **PowerShell 5.1 is the primary shell** and has no `&&` or `||`. Use `;` or
  `cmd-a; if ($?) { cmd-b }`. Do not redirect a native exe's stderr with `2>&1` — 5.1 wraps each
  line in an ErrorRecord and reports failure even on exit 0. Git Bash is available for POSIX
  one-liners.
- Prefer a project's local binary over `npx`: `node web/node_modules/@angular/cli/bin/ng.js build`.

## Layout

```
SPEC.md                              authoritative — read it, don't guess
src/Obhijog.Domain/                  entities, enums, ComplaintStateMachine, SlaPolicy
                                       → .claude/rules/backend-dotnet.md
src/Obhijog.Infrastructure/          DbContext, migrations, seed, blob, sweeper, services
                                       → .claude/rules/data-ef.md
src/Obhijog.Api/                     Program.cs, Endpoints/, auth, ProblemDetails
                                       → .claude/rules/backend-dotnet.md
tests/Obhijog.Tests/                 the four suites
web/                                 Angular 20 workspace
                                       → .claude/rules/frontend-angular.md
infra/                               docker-compose, Bicep (M9)
functions/                           M10 only
docs/adr/                            decisions costly to reverse
```

## Deep rules

Path-scoped. Read the matching one before editing files in that area.

- [`.claude/rules/backend-dotnet.md`](.claude/rules/backend-dotnet.md) — endpoint groups, services,
  `ProblemDetails`, the domain layer's purity
- [`.claude/rules/data-ef.md`](.claude/rules/data-ef.md) — migrations, indexes, the scoping seam,
  concurrency, the seeder
- [`.claude/rules/frontend-angular.md`](.claude/rules/frontend-angular.md) — signals, functional
  guards, the interceptor chain, Material, what the client must not re-derive

## The loop

This repo is built one GitHub issue at a time. Ten issues, one per milestone (SPEC §20).

| Command | Does |
|---|---|
| `/next [issue]` | Runs one full feature cycle: pick → claim → branch → implement + test → push → PR → review → gate → merge → advance |
| `/status` | Branch, open issues, open PRs, CI state, containers |

The cycle itself is [`.claude/skills/feature-cycle/SKILL.md`](.claude/skills/feature-cycle/SKILL.md).
Two hooks enforce the parts that matter: `guard-git.mjs` blocks commits on `main`, force-pushes,
`--no-verify`, and any merge without a review verdict matching the PR's head SHA.

**Only four things may block a merge:** red CI, a test that cannot fail, a violation of a SPEC §9
invariant, and a HIGH security finding. Everything else is advisory and becomes a follow-up issue.
