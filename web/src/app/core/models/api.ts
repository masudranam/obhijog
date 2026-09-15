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

/**
 * SPEC.md §12.2.
 *
 * The first eight are the guard table's vocabulary — the only values that may be sent to
 * `POST /complaints/{id}/transitions`, and the only ones that ever appear in
 * `availableActions`. `Submit` and `Priority` are history-only: the server writes them and
 * a client can never send them.
 */
export type TransitionAction =
  | 'Assign'
  | 'Reassign'
  | 'Recategorize'
  | 'Reject'
  | 'Start'
  | 'Resolve'
  | 'Close'
  | 'Reopen';

export type ComplaintAction = TransitionAction | 'Submit' | 'Priority';

/** SPEC.md §8.4. */
export type ComplaintPriority = 'Low' | 'Normal' | 'High' | 'Critical';

/** SPEC.md §13.3 — the `slaState` list filter. Computed server-side, never here. */
export type SlaState = 'OnTrack' | 'Warning' | 'Breached';

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
  assignedStaffName: string | null;

  /**
   * What *this* caller may do to this complaint right now, straight from the guard table
   * in `Obhijog.Domain` (§12.3). Buttons render from this and from nothing else — a
   * `switch (status)` in a component would be a second copy of the table (§15).
   */
  availableActions: TransitionAction[];
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

/** `GET /complaints/{id}`. */
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

  /**
   * The SLA ladder's rungs, for the F9 timeline. Empty until the M7 sweeper writes one.
   * Carries no reason text — that names the notified supervisor, which is not a citizen's
   * to see (§9.5).
   */
  escalations: EscalationEntry[];

  /** See {@link ComplaintListItem.availableActions}. */
  availableActions: TransitionAction[];
}

export interface EscalationEntry {
  id: string;
  level: number;
  raisedAt: string;
}

/**
 * `POST /complaints/{id}/transitions` — the single write path for a complaint's status
 * (§12). There are no verb endpoints: `action` selects the row of the guard table, and the
 * row says which of the other three fields is required.
 */
export interface TransitionRequest {
  action: TransitionAction;
  note?: string | null;
  assigneeId?: string | null;
  categoryId?: string | null;
}

/** `PUT /complaints/{id}/priority` — §12.4. Status-independent, so not a transition. */
export interface ChangePriorityRequest {
  priority: ComplaintPriority;
}

/**
 * SPEC.md §9.4 — a comment on a complaint.
 *
 * An `isInternal` comment is filtered out of a Citizen's read **in the query**, so this
 * type never carries one for them. The flag is still on the DTO because staff need to see
 * which of their own notes are private.
 */
export interface Comment {
  id: string;
  body: string;
  isInternal: boolean;
  authorName: string;
  createdAt: string;
}

export interface CreateCommentRequest {
  body: string;
  isInternal: boolean;
}

/** `GET /departments/{id}/staff` — the assignee picker's options. */
export interface StaffMember {
  id: string;
  email: string;
  fullName: string;
  role: UserRole;
  isActive: boolean;
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

/**
 * One row of `GET /sla/breaches`. SPEC.md §14 F12.
 *
 * `hoursOverdue` is computed server-side, against the server's clock, so the figure here
 * matches the one in the dashboard and the CSV export. It is a value the client renders,
 * not a threshold it evaluates — what counts as breached was decided by the sweeper and is
 * recorded in `slaBreachedAt` (§11.2, §15).
 */
export interface SlaBreach {
  id: string;
  referenceNumber: string;
  title: string;
  categoryName: string;
  status: ComplaintStatus;
  priority: ComplaintPriority;
  assignedStaffName: string | null;
  slaDueAt: string;
  slaBreachedAt: string;
  escalationLevel: number;
  hoursOverdue: number;
}

/** What `POST /admin/sla/sweep` reports. SPEC.md §14 F11. */
export interface SlaSweepResult {
  examined: number;
  warned: number;
  breached: number;
  escalatedLevel2: number;
  autoClosed: number;
  durationMs: number;
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
