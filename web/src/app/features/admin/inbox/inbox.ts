import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { PageEvent } from '@angular/material/paginator';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';

import { ComplaintService } from '../../../core/complaints/complaint.service';
import { ComplaintStatus, SlaState } from '../../../core/models/api';
import { ComplaintTable } from '../../../shared/complaint-table/complaint-table';
import { ExportButton } from '../../../shared/export-button/export-button';

/** The triage buckets. Each is a §13.3 query, not a client-side partition of one list. */
type Bucket = 'unassigned' | 'open' | 'breached' | 'all';

@Component({
  selector: 'app-inbox',
  imports: [
    MatButtonToggleModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressBarModule,
    MatSelectModule,
    ComplaintTable,
    ExportButton,
  ],
  templateUrl: './inbox.html',
  styleUrl: './inbox.css',
})
export class Inbox {
  private readonly complaints = inject(ComplaintService);

  protected readonly buckets: { value: Bucket; label: string }[] = [
    { value: 'unassigned', label: 'Needs triage' },
    { value: 'open', label: 'Open' },
    { value: 'breached', label: 'Breached' },
    { value: 'all', label: 'All' },
  ];

  protected readonly bucket = signal<Bucket>('unassigned');
  protected readonly search = signal('');
  protected readonly page = signal(1);
  protected readonly pageSize = signal(20);

  /**
   * No `departmentId` is sent. The scoping seam narrows every query to the caller's own
   * department already, and a `departmentId` parameter can never widen it (§9.3) — sending
   * one would only invite the idea that it could.
   */
  protected readonly result = rxResource({
    params: () => ({
      bucket: this.bucket(),
      q: this.search(),
      page: this.page(),
      pageSize: this.pageSize(),
    }),
    stream: ({ params }) =>
      this.complaints.list({
        ...filterFor(params.bucket),
        q: params.q || undefined,
        page: params.page,
        pageSize: params.pageSize,
        sort: 'slaDueAt',
      }),
  });

  /**
   * What the export sends: the active bucket and search, and deliberately **not** the page.
   * "Export what I am looking at" means the filter, not the twenty rows currently on screen
   * — a paged CSV would be a surprising file to receive (F14).
   */
  protected readonly exportFilter = computed(() => ({
    ...filterFor(this.bucket()),
    q: this.search() || undefined,
    sort: 'slaDueAt',
  }));

  protected onBucket(bucket: Bucket): void {
    this.page.set(1);
    this.bucket.set(bucket);
  }

  protected onSearch(term: string): void {
    this.page.set(1);
    this.search.set(term.trim());
  }

  protected onPage(event: PageEvent): void {
    this.pageSize.set(event.pageSize);
    this.page.set(event.pageIndex + 1);
  }
}

const OPEN: ComplaintStatus[] = ['New', 'Assigned', 'InProgress'];
const BREACHED: SlaState = 'Breached';

/** Which §13.3 filters each bucket sends. Every one of them is evaluated server-side. */
function filterFor(bucket: Bucket) {
  switch (bucket) {
    case 'unassigned':
      return { unassigned: true, status: OPEN };
    case 'open':
      return { status: OPEN };
    case 'breached':
      return { slaState: BREACHED };
    default:
      return {};
  }
}
