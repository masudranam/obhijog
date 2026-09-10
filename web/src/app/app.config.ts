import { provideHttpClient, withInterceptors } from '@angular/common/http';
import {
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection,
} from '@angular/core';
import { provideRouter, withComponentInputBinding } from '@angular/router';

import { routes } from './app.routes';
import { authInterceptor } from './core/interceptors/auth.interceptor';
import { errorInterceptor } from './core/interceptors/error.interceptor';

// No animations provider: Angular Material 20 drives its transitions from CSS, so the
// `ng add` schematic does not install `@angular/animations` and nothing here needs it.
export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    // withComponentInputBinding: a route parameter arrives as a component input signal,
    // so a detail component takes `id` directly instead of subscribing to ActivatedRoute.
    provideRouter(routes, withComponentInputBinding()),

    // Order is the chain of SPEC.md §15: attach and refresh first, surface errors last, so
    // a 401 that the refresh recovers from never reaches the snackbar.
    provideHttpClient(withInterceptors([authInterceptor, errorInterceptor])),
  ],
};
