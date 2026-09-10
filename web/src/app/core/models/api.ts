/**
 * Hand-mirrored from the API DTOs. Nothing is generated, so this file moves in the same
 * commit as the endpoint it mirrors (SPEC.md §21.4).
 */

/** SPEC.md §3. Crosses the wire as a string, never a TypeScript enum. */
export type UserRole = 'Citizen' | 'Staff' | 'DeptAdmin';

/** SPEC.md §12.1. */
export type ComplaintStatus =
  | 'New'
  | 'Assigned'
  | 'InProgress'
  | 'Resolved'
  | 'Closed'
  | 'Rejected';

/** SPEC.md §10.1 — the response of register, login and refresh. */
export interface TokenResponse {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
  refreshTokenExpiresAt: string;
}

/** SPEC.md §10.1 — `GET /auth/me`. */
export interface Me {
  id: string;
  email: string;
  fullName: string;
  role: UserRole;
  departmentId: string | null;
}

export interface LoginRequest {
  email: string;
  password: string;
}

export interface RegisterRequest {
  email: string;
  password: string;
  fullName: string;
  phone?: string | null;
}

/** RFC 9457, as SPEC.md §13.1 requires on every non-2xx. */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  traceId?: string;
  errors?: Record<string, string[]>;
}
