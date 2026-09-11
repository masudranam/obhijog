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

/** SPEC.md §12.2. The transition actions plus `Submit`, which only the server writes. */
export type ComplaintAction =
  | 'Assign'
  | 'Reassign'
  | 'Recategorize'
  | 'Reject'
  | 'Start'
  | 'Resolve'
  | 'Close'
  | 'Reopen'
  | 'Submit';

/** SPEC.md §8.4. */
export type ComplaintPriority = 'Low' | 'Normal' | 'High' | 'Critical';

/** SPEC.md §13.1 — a page is these four fields; there is no envelope to unwrap. */
export interface Page<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

/** SPEC.md §13.2 — `GET /categories`. */
export interface Category {
  id: string;
  name: string;
  departmentId: string;
  departmentName: string;
  slaHours: number;
  defaultPriority: ComplaintPriority;
  isActive: boolean;
}

export interface Department {
  id: string;
  code: string;
  name: string;
  isActive: boolean;
}

/** SPEC.md F4 — `POST /complaints`. */
export interface CreateComplaintRequest {
  title: string;
  description: string;
  categoryId: string;
  latitude: number;
  longitude: number;
  addressText?: string | null;
}

/**
 * One row of `GET /complaints`.
 *
 * The SLA fields are carried so the badge renders from them. The client may compute a
 * countdown from `slaDueAt` — that is display arithmetic — but the 80/100/150 thresholds
 * are server-side policy and never reproduced here (§11.2, §15).
 */
export interface ComplaintListItem {
  id: string;
  referenceNumber: string;
  title: string;
  categoryName: string;
  departmentName: string;
  status: ComplaintStatus;
  priority: ComplaintPriority;
  createdAt: string;
  slaDueAt: string;
  slaBreachedAt: string | null;
  slaWarnedAt: string | null;
  escalationLevel: number;
  resolvedAt: string | null;
}

export interface ComplaintHistoryEntry {
  id: string;
  fromStatus: ComplaintStatus;
  toStatus: ComplaintStatus;
  action: ComplaintAction;
  changedByName: string | null;
  changedAt: string;
  note: string | null;
  isSystem: boolean;
}

/**
 * `GET /complaints/{id}`.
 *
 * No `availableActions` yet — the guard table that computes it arrives with M6. The UI
 * therefore renders no action buttons in M4, rather than guessing at them.
 */
export interface ComplaintDetail {
  id: string;
  referenceNumber: string;
  title: string;
  description: string;
  categoryId: string;
  categoryName: string;
  departmentId: string;
  departmentName: string;
  status: ComplaintStatus;
  priority: ComplaintPriority;
  latitude: number;
  longitude: number;
  addressText: string | null;
  assignedStaffId: string | null;
  assignedStaffName: string | null;
  createdAt: string;
  slaDueAt: string;
  slaWarnedAt: string | null;
  slaBreachedAt: string | null;
  escalationLevel: number;
  resolvedAt: string | null;
  closedAt: string | null;
  rejectionReason: string | null;
  resolutionNote: string | null;
  reopenCount: number;
  history: ComplaintHistoryEntry[];
}

/**
 * `GET /complaints/by-reference/{ref}` — the redacted projection of §9.5.
 *
 * Deliberately a separate type from {@link ComplaintDetail} rather than a Partial of it:
 * the absence of identity, coordinates and free text is the security boundary, and a type
 * that could widen into the full shape by accident would not express that.
 */
export interface PublicComplaint {
  referenceNumber: string;
  categoryName: string;
  departmentName: string;
  status: ComplaintStatus;
  createdAt: string;
  slaDueAt: string;
  isSlaBreached: boolean;
  resolvedAt: string | null;
  history: PublicHistoryEntry[];
}

export interface PublicHistoryEntry {
  changedAt: string;
  toStatus: ComplaintStatus;
}

/**
 * SPEC.md §8.7, F6.
 *
 * `readUrl` is a short-lived SAS onto a private container. It is served on the DTO because
 * an `<img>` cannot carry a bearer token, so it is the only way a thumbnail renders. It
 * expires — treat it as good for this page view, not as a permalink.
 */
export interface Attachment {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  uploadedAt: string;
  readUrl: string;
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
