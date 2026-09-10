import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, map, of } from 'rxjs';

import { AuthService } from '../auth/auth.service';
import { UserRole } from '../models/api';

/**
 * Functional guards, per SPEC.md §15. No class-based guards anywhere.
 *
 * These decide **routing**, not permission. Every route they protect still has a
 * server-side guard, and the backend tests prove it (§21.5) — a hidden route is not
 * security, and treating it as such is how a client-side check becomes the only check.
 */

/** Requires a session. Loads `/auth/me` on a cold start so a reload does not bounce to login. */
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  if (!auth.accessToken) {
    return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
  }

  // A token survived a page reload but the in-memory session did not. Resolve it before
  // deciding, rather than treating a refresh of the browser as a logout.
  return auth.loadMe().pipe(
    map(() => true),
    catchError(() => {
      auth.clearSession();
      return of(router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }));
    }),
  );
};

/**
 * Requires one of the given roles. Sends an authenticated caller with the wrong role to
 * their own landing route rather than to login — they are signed in, just not here.
 */
export function roleGuard(...roles: UserRole[]): CanActivateFn {
  return (route, state) => {
    const auth = inject(AuthService);
    const router = inject(Router);

    const decide = () => {
      const role = auth.role();

      if (role && roles.includes(role)) {
        return true;
      }

      return router.createUrlTree([role ? auth.landingRoute(role) : '/login']);
    };

    if (auth.isAuthenticated()) {
      return decide();
    }

    if (!auth.accessToken) {
      return router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });
    }

    return auth.loadMe().pipe(
      map(() => decide()),
      catchError(() => {
        auth.clearSession();
        return of(router.createUrlTree(['/login'], { queryParams: { returnUrl: state.url } }));
      }),
    );
  };
}
