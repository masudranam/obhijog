import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { Observable, tap } from 'rxjs';

import { environment } from '../../../environments/environment';
import { LoginRequest, Me, RegisterRequest, TokenResponse, UserRole } from '../models/api';

const ACCESS_TOKEN_KEY = 'obhijog.accessToken';
const REFRESH_TOKEN_KEY = 'obhijog.refreshToken';

/**
 * Session state and the auth endpoints of SPEC.md §10.1.
 *
 * State is signals, not a BehaviorSubject store (SPEC.md §15). Tokens live in
 * `localStorage` so a reload does not sign the user out; `sessionStorage` would be
 * marginally safer against a stale shared machine but neither defends against XSS, and
 * the honest mitigation is the 15-minute access token plus rotating refresh (§10.2).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly router = inject(Router);
  private readonly baseUrl = `${environment.apiBaseUrl}/auth`;

  private readonly _me = signal<Me | null>(null);

  readonly me = this._me.asReadonly();
  readonly isAuthenticated = computed(() => this._me() !== null);
  readonly role = computed<UserRole | null>(() => this._me()?.role ?? null);

  get accessToken(): string | null {
    return this.read(ACCESS_TOKEN_KEY);
  }

  get refreshToken(): string | null {
    return this.read(REFRESH_TOKEN_KEY);
  }

  login(request: LoginRequest): Observable<TokenResponse> {
    return this.http
      .post<TokenResponse>(`${this.baseUrl}/login`, request)
      .pipe(tap((tokens) => this.store(tokens)));
  }

  register(request: RegisterRequest): Observable<TokenResponse> {
    return this.http
      .post<TokenResponse>(`${this.baseUrl}/register`, request)
      .pipe(tap((tokens) => this.store(tokens)));
  }

  /**
   * Called only by the refresh interceptor, which serialises it — see
   * `auth.interceptor.ts`. Calling it directly from a component would defeat the
   * single-in-flight guarantee of §15.
   */
  refresh(): Observable<TokenResponse> {
    return this.http
      .post<TokenResponse>(`${this.baseUrl}/refresh`, { refreshToken: this.refreshToken })
      .pipe(tap((tokens) => this.store(tokens)));
  }

  loadMe(): Observable<Me> {
    return this.http.get<Me>(`${this.baseUrl}/me`).pipe(tap((me) => this._me.set(me)));
  }

  /**
   * Revokes the presented refresh token server-side, then clears the session regardless.
   * A failed revoke must still log the user out locally — leaving them apparently signed
   * in because the network blipped is the worse outcome.
   */
  logout(): void {
    const refreshToken = this.refreshToken;

    if (refreshToken) {
      this.http.post(`${this.baseUrl}/logout`, { refreshToken }).subscribe({
        next: () => undefined,
        error: () => undefined,
      });
    }

    this.clearSession();
    void this.router.navigate(['/login']);
  }

  /** Clears local state without calling the server. Used by the interceptor on a failed refresh. */
  clearSession(): void {
    this.remove(ACCESS_TOKEN_KEY);
    this.remove(REFRESH_TOKEN_KEY);
    this._me.set(null);
  }

  /** The landing route for the caller's role. SPEC.md §14 F3. */
  landingRoute(role: UserRole): string {
    switch (role) {
      case 'Citizen':
        return '/my';
      case 'Staff':
        return '/queue';
      case 'DeptAdmin':
        return '/inbox';
    }
  }

  private store(tokens: TokenResponse): void {
    this.write(ACCESS_TOKEN_KEY, tokens.accessToken);
    this.write(REFRESH_TOKEN_KEY, tokens.refreshToken);
  }

  // localStorage throws in some privacy modes rather than returning null, so every access
  // is guarded — an unavailable store must degrade to "signed out", never to a crash.
  private read(key: string): string | null {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  private write(key: string, value: string): void {
    try {
      localStorage.setItem(key, value);
    } catch {
      // Ignored: the session simply will not survive a reload.
    }
  }

  private remove(key: string): void {
    try {
      localStorage.removeItem(key);
    } catch {
      // Ignored.
    }
  }
}
