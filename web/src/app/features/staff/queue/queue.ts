import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { PageEvent } from '@angular/material/paginator';

import { ComplaintService } from '../../../core/complaints/complaint.service';
import { ComplaintStatus } from '../../../core/models/api';
import { ComplaintTable } from '../../../shared/complaint-table/complaint-table';

/**
 * F7 — a staff member's own queue.
 *
 * `assignedToMe` is a server-side filter (§13.3), not a client one: the scoping seam
 * already narrows the query to this caller's department, and asking the server for "mine"
 * keeps the definition of mine in one place.
 *
 * There are no action buttons on this screen. Actions belong to the detail view, where the
 * server has said which are available for that particular complaint.
 */
@Component({
  selector: 'app-queue',
  imports: [
    MatButtonToggleModule,
    MatCardModule,
    MatProgressBarModule,
    ComplaintTable,
  ],
  templateUrl: './queue.html',
  styleUrl: './queue.css',
})
export class Queue {
  private readonly complaints = inject(ComplaintService);

  protected readonly columns = ['reference', 'title', 'status', 'priority', 'sla', 'created'];

  /** Open work first — that is what a queue is for. 'all' includes what is already done. */
  protected readonly scope = signal<'open' | 'all'>('open');
  protected readonly page = signal(1);
  protected readonly pageSize = signal(20);

  protected readonly result = rxResource({
    params: () => ({
      scope: this.scope(),
      page: this.page(),
      pageSize: this.pageSize(),
    }),
    stream: ({ params }) =>
      this.complaints.list({
        assignedToMe: true,
        status: params.scope === 'open' ? OPEN : undefined,
        page: params.page,
        pageSize: params.pageSize,

        // Most urgent first: the deadline is the queue's ordering, not the report date.
        sort: 'slaDueAt',
      }),
  });

  protected onScope(scope: 'open' | 'all'): void {
    this.page.set(1);
    this.scope.set(scope);
  }

  protected onPage(event: PageEvent): void {
    this.pageSize.set(event.pageSize);
    this.page.set(event.pageIndex + 1);
  }
}

/** Work a staff member still has to do. Display grouping, not a rule the server enforces. */
const OPEN: ComplaintStatus[] = ['Assigned', 'InProgress'];
