import { Routes } from '@angular/router';

import { authGuard, roleGuard } from './core/guards/auth.guard';

/**
 * One lazy `loadComponent` per feature, guarded by role (SPEC.md §15).
 *
 * The guards decide routing, not permission — every route here also has a server-side
 * guard, and the backend tests prove it (§21.5).
 */
export const routes: Routes = [
  {
    path: '',
    pathMatch: 'full',
    loadComponent: () => import('./features/home/home').then((m) => m.Home),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login').then((m) => m.Login),
  },
  {
    path: 'register',
    loadComponent: () => import('./features/auth/register/register').then((m) => m.Register),
  },
  {
    // Unauthenticated by design — the one public read in the app (§9.5).
    path: 'track',
    loadComponent: () => import('./features/track/track').then((m) => m.Track),
  },
  {
    path: 'my',
    canActivate: [authGuard, roleGuard('Citizen')],
    loadComponent: () =>
      import('./features/citizen/my-complaints/my-complaints').then((m) => m.MyComplaints),
  },
  {
    path: 'my/new',
    canActivate: [authGuard, roleGuard('Citizen')],
    loadComponent: () => import('./features/citizen/submit/submit').then((m) => m.SubmitComplaint),
  },
  {
    // After 'my/new', so the literal segment is matched before the parameter. Kept as a
    // citizen-facing alias of /complaints/:id — the links a citizen already has still work.
    path: 'my/:id',
    canActivate: [authGuard, roleGuard('Citizen')],
    loadComponent: () =>
      import('./features/complaints/complaint-detail/complaint-detail').then(
        (m) => m.ComplaintDetailPage,
      ),
  },
  {
    // One detail screen for all three roles. No role guard beyond being signed in: what
    // differs between them is what the server returns, and a complaint outside the
    // caller's scope is a 404 rather than a route they cannot reach (§9.2, §9.3).
    path: 'complaints/:id',
    canActivate: [authGuard],
    loadComponent: () =>
      import('./features/complaints/complaint-detail/complaint-detail').then(
        (m) => m.ComplaintDetailPage,
      ),
  },
  {
    path: 'queue',
    canActivate: [authGuard, roleGuard('Staff')],
    loadComponent: () => import('./features/staff/queue/queue').then((m) => m.Queue),
  },
  {
    path: 'inbox',
    canActivate: [authGuard, roleGuard('DeptAdmin')],
    loadComponent: () => import('./features/admin/inbox/inbox').then((m) => m.Inbox),
  },
  {
    // DeptAdmin only, matching `GET /sla/breaches` (§13.2). The guard is routing, not
    // permission — the endpoint refuses anyone else regardless of what the router allows.
    path: 'breaches',
    canActivate: [authGuard, roleGuard('DeptAdmin')],
    loadComponent: () => import('./features/admin/breaches/breaches').then((m) => m.Breaches),
  },
  {
    path: '**',
    redirectTo: '',
  },
];
