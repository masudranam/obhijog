import { Routes } from '@angular/router';

/**
 * One lazy `loadComponent` per feature, guarded by role once M3 lands (SPEC.md §15).
 * M1 ships the shell and a single route so the router is wired and provably renders.
 */
export const routes: Routes = [
  {
    path: '',
    loadComponent: () => import('./features/home/home').then((m) => m.Home),
  },
  {
    path: '**',
    redirectTo: '',
  },
];
