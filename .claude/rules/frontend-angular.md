# Rules — Angular 20 web app

Applies to `web/`.
Read [SPEC.md §15](../../SPEC.md#15-frontend-architecture) alongside this.

## Standalone and signals

Standalone components only — **no NgModules**, anywhere, including for testing.

State is signals: `signal`, `computed`, `linkedSignal`, and `resource` / `httpResource` for reads.
No NgRx. No RxJS `BehaviorSubject` store. `HttpClient` is fine, but a bare `.subscribe()` in a
component is not — use `resource` or `toSignal` so teardown is handled for you.

Routes are lazy, one `loadComponent` per feature, guarded by role:

```ts
{ path: 'inbox', canActivate: [authGuard, deptAdminGuard],
  loadComponent: () => import('./features/admin/inbox/inbox.component') }
```

Guards are functional (`CanActivateFn`). No class-based guards.

## The client re-derives nothing

This is the rule most likely to be broken, and it is the one that matters most.

**Action buttons render from `availableActions` returned by the API.** The guard table lives in
`MunicipalSla.Domain` and nowhere else. A `canAssign()` helper in a component, a
`switch (status)` that decides which buttons to show, a constant listing which roles may resolve —
each is a second copy of the guard table that will drift, and each is a review finding.

```ts
// yes
@if (complaint().availableActions.includes('resolve')) { <button …>Resolve</button> }

// no — this is the guard table, copied
@if (complaint().status === 'InProgress' && user().role === 'Staff') { … }
```

**SLA badges render from `slaDueAt`, `slaBreachedAt` and `escalationLevel`.** The 80/100/150
thresholds are server-side policy (SPEC §11.2). The client may compute a *countdown* from
`slaDueAt` — that is display arithmetic — but it must not decide what counts as a warning.

The UI hides what a user cannot do purely for looks. **Every hidden control still has a server-side
guard**, and the backend tests prove it (SPEC §21.5). Never treat a hidden button as security.

## The interceptor chain

One chain, in this order:

1. **attach** the bearer token
2. **refresh** once on `401` — a single in-flight refresh with queued retries, never one refresh per
   failed request; on refresh failure, clear the session and route to login
3. **surface** errors: read the `ProblemDetails` `title` and `detail` and show them in a Material
   snackbar

Nothing else catches HTTP errors. A component-level `catchError` that swallows a failure into a
blank screen is worse than the error.

## API models

One hand-maintained file, `core/models/api.ts`, mirroring the API DTOs. Nothing is generated, so
**it moves in the same commit as the endpoint it mirrors** (SPEC §21.4).

Enums cross the wire as strings and are mirrored as string union types, not TypeScript `enum`:

```ts
export type ComplaintStatus =
  | 'New' | 'Assigned' | 'InProgress' | 'Resolved' | 'Closed' | 'Rejected';
```

Pages are `{ items, page, pageSize, total }`. There is no response envelope to unwrap.

## Material, not Tailwind

Angular Material supplies the table, paginator, dialog, snackbar, chips and form fields — which is
most of this application. No Tailwind, no second component library, no hand-rolled table.

Shared components live in `shared/` and are used, not re-implemented per feature: `sla-badge`,
`status-chip`, `priority-chip`, `complaint-timeline`, `attachment-gallery`, `map-picker`,
`confirm-dialog`.

## The map

Leaflet with OpenStreetMap tiles, confined to one `map-picker` component: browser geolocation for
"use my location", a draggable pin, emitting `{ lat, lng }`; a read-only mode for the detail screen.

No API key, no billing account, and no second mapping library. Leaflet's CSS is imported once in
`styles.css`, not per component.

## Configuration

`environment.ts` holds `apiBaseUrl` and nothing secret. It must match the API port and
`Cors:Origins` — **all four change together** (SPEC §19). A mismatch here 404s or CORS-fails every
request at the network layer and looks convincingly like an application bug, so check it first when
nothing works at all.

## The gate

`npm run build --prefix web` must be **warning-clean**. There are zero frontend tests by decision
(SPEC D6), which means the build is the only automated check the frontend gets — a warning you
tolerate is a check you have given up.
