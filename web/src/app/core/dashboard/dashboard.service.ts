import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { DashboardSummary } from '../models/api';

/**
 * `GET /dashboard/summary`. SPEC.md §13.2, §14 F13.
 *
 * One call, no arguments. The scope is the caller's own and the server applies it — there is
 * no `departmentId` to pass and no role to declare, because a parameter that could change
 * which complaints are counted would be a parameter that could widen scope (§9.3).
 */
@Injectable({ providedIn: 'root' })
export class DashboardService {
  private readonly http = inject(HttpClient);

  summary(): Observable<DashboardSummary> {
    return this.http.get<DashboardSummary>(`${environment.apiBaseUrl}/dashboard/summary`);
  }
}
