import {
  HttpErrorResponse,
  HttpEvent,
  HttpHandlerFn,
  HttpInterceptorFn,
  HttpRequest,
} from '@angular/common/http';
import { inject } from '@angular/core';
import { Observable, throwError } from 'rxjs';
import { catchError, finalize, map, shareReplay, switchMap } from 'rxjs/operators';

import { environment } from '../../../environments/environment';
import { AuthService } from '../auth/auth.service';

/**
 * Stage 1 and 2 of the chain in SPEC.md §15: attach the bearer token, then refresh **once**
 * on a 401 with queued retries.
 *
 * The single-in-flight rule matters more than it looks. Without it, ten parallel requests
 * that all 401 produce ten refreshes; the first rotates the token and the other nine
 * present one that is now revoked — which §10.2 treats as reuse and answers by revoking the
 * whole family. Naive per-request refresh does not merely waste calls, it logs the user out.
 */

// Module-scoped rather than per-request: the interceptor function runs once per request, so
// shared state is the only way to serialise them. Holding the *observable* rather than a
// boolean flag is what makes the failure path work — every queued request is subscribed to
// the same stream, so they all receive the error instead of waiting for a token that is
// never going to arrive.
let refreshInFlight: Observable<string> | null = null;

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

      return retryAfterRefresh(request, next, auth);
    }),
  );
};

function retryAfterRefresh(
  request: HttpRequest<unknown>,
  next: HttpHandlerFn,
  auth: AuthService,
): Observable<HttpEvent<unknown>> {
  return refreshOnce(auth).pipe(switchMap((token) => next(withBearer(request, token))));
}

/**
 * One refresh per wave of 401s, shared by every caller in that wave.
 *
 * `shareReplay` with `refCount: false` means the HTTP call is made once no matter how many
 * requests queue behind it, and a subscriber arriving a tick late still gets the token
 * rather than starting a second refresh. `finalize` releases the slot afterwards, so a
 * later 401 — a genuinely expired session, an hour on — refreshes again instead of
 * replaying a stale result.
 */
function refreshOnce(auth: AuthService): Observable<string> {
  refreshInFlight ??= auth.refresh().pipe(
    map((tokens) => tokens.accessToken),
    catchError((error: unknown) => {
      // The refresh itself failed: the session is genuinely over. Clearing it here rather
      // than per waiter means it happens once however many requests were queued, and the
      // error reaches all of them.
      auth.clearSession();
      return throwError(() => error);
    }),
    finalize(() => {
      refreshInFlight = null;
    }),
    shareReplay({ bufferSize: 1, refCount: false }),
  );

  return refreshInFlight;
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
