import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { MatSnackBar } from '@angular/material/snack-bar';
import { throwError } from 'rxjs';
import { catchError } from 'rxjs/operators';

import { ProblemDetails } from '../models/api';

/**
 * Stage 3 of the chain in SPEC.md §15: surface the `ProblemDetails` `title` and `detail`
 * in a Material snackbar.
 *
 * This is the **only** place HTTP errors are shown. A component-level `catchError` that
 * swallows a failure into a blank screen is worse than the error, so this rethrows after
 * displaying — a caller that wants to react still can, it just cannot silence.
 */
export const errorInterceptor: HttpInterceptorFn = (request, next) => {
  const snackBar = inject(MatSnackBar);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (error instanceof HttpErrorResponse) {
        // 401 is handled by the auth interceptor, which refreshes and retries. Announcing
        // it here would flash a spurious error during a refresh the user never sees.
        if (error.status !== 401) {
          snackBar.open(describe(error), 'Dismiss', { duration: 6000 });
        }
      }

      return throwError(() => error);
    }),
  );
};

function describe(error: HttpErrorResponse): string {
  if (error.status === 0) {
    return 'Cannot reach the server. Check that the API is running.';
  }

  const problem = error.error as ProblemDetails | null;

  // Validation failures carry the useful text in `errors`, not in `detail`.
  if (problem?.errors) {
    const first = Object.values(problem.errors).flat()[0];
    if (first) {
      return first;
    }
  }

  return problem?.detail ?? problem?.title ?? `Request failed (${error.status}).`;
}
