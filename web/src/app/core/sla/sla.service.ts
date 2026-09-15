import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';

import { environment } from '../../../environments/environment';
import { SlaBreach, SlaSweepResult } from '../models/api';

/**
 * The two SLA routes of SPEC.md §13.2.
 *
 * Thin by design, like `ComplaintService`. No thresholds live here: the 80/100/150 rungs are
 * server policy (§11.2), and a client that knew them would be a second copy that drifts
 * (§15, CLAUDE.md non-negotiable 6).
 */
@Injectable({ providedIn: 'root' })
export class SlaService {
  private readonly http = inject(HttpClient);

  /** Breached complaints still open, in the caller's own department. Scoped server-side. */
  breaches(): Observable<SlaBreach[]> {
    return this.http.get<SlaBreach[]>(`${environment.apiBaseUrl}/sla/breaches`);
  }

  /**
   * Runs one pass by hand. The same method the background sweeper calls every
   * `Sla:SweepIntervalSeconds`, so this is a trigger and never a second implementation.
   */
  sweep(): Observable<SlaSweepResult> {
    return this.http.post<SlaSweepResult>(`${environment.apiBaseUrl}/admin/sla/sweep`, {});
  }
}
