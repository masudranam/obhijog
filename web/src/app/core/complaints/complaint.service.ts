import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  Category,
  ChangePriorityRequest,
  Comment,
  ComplaintDetail,
  ComplaintListItem,
  ComplaintPriority,
  ComplaintStatus,
  CreateCommentRequest,
  CreateComplaintRequest,
  Page,
  PublicComplaint,
  SlaState,
  StaffMember,
  TransitionRequest,
} from '../models/api';

/** SPEC.md §13.3. */
export interface ComplaintListParams {
  status?: ComplaintStatus[];
  priority?: ComplaintPriority[];
  categoryId?: string;
  departmentId?: string;
  assignedToMe?: boolean;
  unassigned?: boolean;
  slaState?: SlaState;
  q?: string;
  sort?: string;
  page?: number;
  pageSize?: number;
}

/**
 * The complaint endpoints of SPEC.md §13.2.
 *
 * Thin on purpose: no filtering, no scoping and no SLA arithmetic happen here. The server
 * decides all three and this service carries the answer (§15).
 */
@Injectable({ providedIn: 'root' })
export class ComplaintService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = `${environment.apiBaseUrl}/complaints`;

  create(request: CreateComplaintRequest): Observable<ComplaintDetail> {
    return this.http.post<ComplaintDetail>(this.baseUrl, request);
  }

  list(params: ComplaintListParams = {}): Observable<Page<ComplaintListItem>> {
    return this.http.get<Page<ComplaintListItem>>(this.baseUrl, {
      params: this.toParams(params),
    });
  }

  /**
   * `GET /complaints/export`. SPEC.md §14 F14.
   *
   * Built from {@link toParams}, the very same query the list sends — which is the client
   * half of F14's "the export applies the same filter as the list". The server enforces the
   * other half by sharing `ScopedAndFiltered`; between them there is no place for the two to
   * drift.
   *
   * A blob rather than a plain `<a href>`: the endpoint needs the bearer token, and an
   * anchor cannot carry one. The interceptor attaches it to this request as it would any
   * other.
   */
  export(params: ComplaintListParams = {}): Observable<Blob> {
    return this.http.get(`${this.baseUrl}/export`, {
      params: this.toParams(params),
      responseType: 'blob',
    });
  }

  /** The §13.3 query string, built once for both the list and the export. */
  private toParams(params: ComplaintListParams): HttpParams {
    let httpParams = new HttpParams();

    for (const status of params.status ?? []) {
      httpParams = httpParams.append('status', status);
    }

    for (const priority of params.priority ?? []) {
      httpParams = httpParams.append('priority', priority);
    }

    if (params.categoryId) {
      httpParams = httpParams.set('categoryId', params.categoryId);
    }
    if (params.departmentId) {
      httpParams = httpParams.set('departmentId', params.departmentId);
    }
    if (params.assignedToMe) {
      httpParams = httpParams.set('assignedToMe', true);
    }
    if (params.unassigned) {
      httpParams = httpParams.set('unassigned', true);
    }
    if (params.slaState) {
      httpParams = httpParams.set('slaState', params.slaState);
    }
    if (params.q) {
      httpParams = httpParams.set('q', params.q);
    }
    if (params.sort) {
      httpParams = httpParams.set('sort', params.sort);
    }
    if (params.page) {
      httpParams = httpParams.set('page', params.page);
    }
    if (params.pageSize) {
      httpParams = httpParams.set('pageSize', params.pageSize);
    }

    return httpParams;
  }

  get(id: string): Observable<ComplaintDetail> {
    return this.http.get<ComplaintDetail>(`${this.baseUrl}/${id}`);
  }

  /** §9.5 — unauthenticated. The interceptor still runs; there is simply no token to attach. */
  getByReference(reference: string): Observable<PublicComplaint> {
    return this.http.get<PublicComplaint>(
      `${this.baseUrl}/by-reference/${encodeURIComponent(reference)}`,
    );
  }

  categories(): Observable<Category[]> {
    return this.http.get<Category[]>(`${environment.apiBaseUrl}/categories`);
  }

  /**
   * The one write path for a complaint's status (§12). Note there is no `assign()`,
   * `resolve()` or `close()` here: verb methods would invite a component to decide which
   * one is legal, and that decision belongs to the guard table on the server.
   */
  transition(id: string, request: TransitionRequest): Observable<ComplaintDetail> {
    return this.http.post<ComplaintDetail>(`${this.baseUrl}/${id}/transitions`, request);
  }

  /** §12.4 — not a transition; priority never moves the complaint. Dept Admin only. */
  changePriority(id: string, request: ChangePriorityRequest): Observable<ComplaintDetail> {
    return this.http.put<ComplaintDetail>(`${this.baseUrl}/${id}/priority`, request);
  }

  comments(id: string): Observable<Comment[]> {
    return this.http.get<Comment[]>(`${this.baseUrl}/${id}/comments`);
  }

  addComment(id: string, request: CreateCommentRequest): Observable<Comment> {
    return this.http.post<Comment>(`${this.baseUrl}/${id}/comments`, request);
  }

  /** `GET /departments/{id}/staff` — the assignee picker. Dept Admin only, own dept only. */
  departmentStaff(departmentId: string): Observable<StaffMember[]> {
    return this.http.get<StaffMember[]>(
      `${environment.apiBaseUrl}/departments/${departmentId}/staff`,
    );
  }
}
