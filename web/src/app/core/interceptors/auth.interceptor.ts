import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { BehaviorSubject, Observable, filter, switchMap, take, throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

/**
 * Stage 1 and 2 of the chain in SPEC.md §15: attach the bearer token, then refresh **once**
 * on a 401 with queued retries.
 *
 * The single-in-flight rule matters more than it looks. Without it, ten parallel requests
 * that all 401 produce ten refreshes; the first rotates the token and the other nine
 * present one that is now revoked — which §10.1 treats as reuse and answers by revoking
 * the whole family. Naive per-request refresh does not merely waste calls, it logs the
 * user out.
 */

// Module-scoped rather than per-request: the interceptor function is invoked once per
// request, so shared state is the only way to serialise them.
let refreshInFlight = false;
const refreshed = new BehaviorSubject<string | null>(null);

export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);

  // The auth endpoints must not carry a stale bearer token, and refresh/login must never
  // recurse into this handler's own 401 branch.
  if (isAuthEndpoint(request.url)) {
    return next(request);
  }

  const token = auth.accessToken;
  const authorized = token ? withBearer(request, token) : request;

  return next(authorized).pipe(
    catchError((error: unknown) => {
      if (!(error instanceof HttpErrorResponse) || error.status !== 401) {
        return throwError(() => error);
      }

      if (!auth.refreshToken) {
        auth.clearSession();
        return throwError(() => error);
      }

      return handle401(request, next, auth);
    }),
  );
};

function handle401(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
): Observable<HttpEvent<unknown>> {
  if (refreshInFlight) {
    // Queue behind the refresh already running, then retry with whatever it produced.
    return refreshed.pipe(
      filter((token): token is string => token !== null),
      take(1),
      switchMap((token) => next(withBearer(request, token))),
    );
  }

  refreshInFlight = true;
  refreshed.next(null);

  return auth.refresh().pipe(
    switchMap((tokens) => {
      refreshInFlight = false;
      refreshed.next(tokens.accessToken);
      return next(withBearer(request, tokens.accessToken));
    }),
    catchError((refreshError: unknown) => {
      // The refresh itself failed: the session is genuinely over. Waiters must be released
      // or they hang forever, so the flag is cleared before rethrowing.
      refreshInFlight = false;
      auth.clearSession();
      return throwError(() => refreshError);
    }),
  );
}

function withBearer(request: HttpRequest<unknown>, token: string): HttpRequest<unknown> {
  return request.clone({ setHeaders: { Authorization: `Bearer ${token}` } });
}

function isAuthEndpoint(url: string): boolean {
  const base = `${environment.apiBaseUrl}/auth`;
  return (
    url.startsWith(`${base}/login`) ||
    url.startsWith(`${base}/register`) ||
    url.startsWith(`${base}/refresh`)
  );
}
