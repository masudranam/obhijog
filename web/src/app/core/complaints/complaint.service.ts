import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import {
  Category,
  ComplaintDetail,
  ComplaintListItem,
  ComplaintStatus,
  CreateComplaintRequest,
  Page,
  PublicComplaint,
} from '../models/api';

/** The subset of §13.3 the M4 screens use. The rest arrives with the screens that need it. */
export interface ComplaintListParams {
  status?: ComplaintStatus[];
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
    let httpParams = new HttpParams();

    for (const status of params.status ?? []) {
      httpParams = httpParams.append('status', status);
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

    return this.http.get<Page<ComplaintListItem>>(this.baseUrl, { params: httpParams });
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
}
