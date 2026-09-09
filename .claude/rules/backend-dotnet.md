# Rules — .NET API and Domain

Applies to `src/Obhijog.Api/` and `src/Obhijog.Domain/`.
Read [SPEC.md §16](../../SPEC.md#16-backend-architecture) alongside this.

## The domain layer is pure

`Obhijog.Domain` holds entities, enums, `ComplaintStateMachine` and `SlaPolicy` — and **takes
no package reference to EF Core, ASP.NET Core, or anything that touches I/O.** No `DbContext`, no
`IQueryable`, no `HttpContext`, no logger.

Both `ComplaintStateMachine` and `SlaPolicy` are pure functions over values plus an injected
`TimeProvider`. That purity is the only reason the two most valuable test suites are cheap to write.
Adding a dependency here costs you the tests.

## Endpoints do three things

Minimal APIs, one file per feature under `Api/Endpoints/`, each exposing:

```csharp
public static RouteGroupBuilder MapComplaintEndpoints(this IEndpointRouteBuilder app)
```

An endpoint may: **bind and validate the request · call one service · map the result to a status
code.** That is all.

Never in an endpoint file:

- an EF Core query or a `DbContext` injection
- a hand-written scope filter (`Where(c => c.CitizenId == …)`) — use `ComplaintQueryScope`
- business logic, including any SLA arithmetic or status assignment
- a `try/catch` that returns `BadRequest` — throw and let the exception handler map it

## Errors are ProblemDetails, mapped in one place

One `IExceptionHandler` owns the whole mapping:

| Exception | Status |
|---|---|
| `NotFoundException` | `404` |
| `ForbiddenException` | `403` |
| `InvalidTransitionException` | `409` |
| `DbUpdateConcurrencyException` | `409` |
| `ValidationException` | `400` |

Every response carries `type`, `title`, `status`, `detail`, `traceId`, plus `errors` for validation
failures. No custom envelope. No `{ "success": false }`. `422` is never used.

**The `404`-not-`403` rule (SPEC §9.2) is a domain decision, not a presentation one** — the service
throws `NotFoundException` for an out-of-scope complaint. Do not let an endpoint decide which to
return.

## Time

`TimeProvider` is injected. **No `DateTimeOffset.UtcNow` or `DateTime.Now` outside `Program.cs`.**
This is not a style preference — the SLA suites in SPEC §18 need a fake clock, and a direct call
makes the behaviour untestable.

## Authorization

Named policies in one place, never role strings scattered through endpoint files:

```csharp
app.MapPost("/complaints", …).RequireAuthorization(Policies.Citizen);
```

`Policies.Citizen`, `Policies.Staff`, `Policies.DeptAdmin`, `Policies.StaffOrAdmin`.

A scoped `CurrentUser` service reads `sub`, `role` and `dept` from the claims once per request. It
is what `ComplaintQueryScope` consumes. Do not re-parse claims in a service.

**Action permission belongs to the guard table, not to a policy.** A policy answers "may this role
reach this route"; the guard table answers "may this user perform this action on this complaint
right now". Both are needed and they are not interchangeable.

## Transitions

There is exactly one write path for a complaint's status: `ComplaintTransitionService`, consulting
`ComplaintStateMachine`, reached through `POST /complaints/{id}/transitions`.

`complaint.Status = …` appears nowhere else in the codebase. Not in a seeder, not in a test helper
that could be replaced by calling the real path, not in a "quick fix".

Every accepted transition writes exactly one `ComplaintStatusHistory` row **in the same transaction**
as the mutation. The response returns the complaint's new `availableActions`, computed from the
guard table — the client renders buttons from that and never re-derives it.

## Configuration

Bind options classes with validation at startup, and **fail fast**:

```csharp
builder.Services.AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection("Jwt"))
    .ValidateDataAnnotations()
    .Validate(o => o.SigningKey.Length >= 32, "Jwt:SigningKey must be at least 32 bytes")
    .ValidateOnStart();
```

A missing connection string, a missing signing key, or a key shorter than 32 bytes **fails
startup**. Never fall back to a built-in default — a development default that reaches production is
the same bug as a hard-coded secret.

## Logging

`ILogger<T>` with structured properties, never interpolated strings:

```csharp
logger.LogInformation("Sweep complete: {Examined} examined, {Breached} breached in {Duration}ms",
    examined, breached, duration);
```

The `traceId` in every `ProblemDetails` must be the same id that appears in the logs for that
request. One structured line per sweep pass (SPEC §11.6) — that is the observability story for the
feature that has no UI of its own.
