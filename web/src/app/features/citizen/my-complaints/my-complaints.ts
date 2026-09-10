import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';

import { ComplaintService } from '../../../core/complaints/complaint.service';
import { SlaBadge } from '../../../shared/sla-badge/sla-badge';
import { StatusChip } from '../../../shared/status-chip/status-chip';

/**
 * F5 — a citizen's own complaints.
 *
 * The list is scoped by the server, not here: `GET /complaints` already returns only what
 * this caller may see (§9.3). There is no client-side filter on citizen id, and adding one
 * would be a second copy of the scoping rule.
 */
@Component({
  selector: 'app-my-complaints',
  imports: [
    DatePipe,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatPaginatorModule,
    MatProgressBarModule,
    MatTableModule,
    RouterLink,
    SlaBadge,
    StatusChip,
  ],
  templateUrl: './my-complaints.html',
  styleUrl: './my-complaints.css',
})
export class MyComplaints {
  private readonly complaints = inject(ComplaintService);

  protected readonly columns = ['reference', 'title', 'status', 'sla', 'created'] as const;

  protected readonly page = signal(1);
  protected readonly pageSize = signal(10);
  protected readonly search = signal('');

  protected readonly result = rxResource({
    params: () => ({ page: this.page(), pageSize: this.pageSize(), q: this.search() }),
    stream: ({ params }) =>
      this.complaints.list({
        page: params.page,
        pageSize: params.pageSize,
        q: params.q || undefined,
        sort: '-createdAt',
      }),
  });

  protected onPage(event: PageEvent): void {
    this.pageSize.set(event.pageSize);
    this.page.set(event.pageIndex + 1);
  }

  protected onSearch(term: string): void {
    this.page.set(1);
    this.search.set(term.trim());
  }
}
