import { Component, ChangeDetectionStrategy, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import { map } from 'rxjs';

import {
  ComplaintListParams,
  ComplaintService,
} from '../../core/complaints/complaint.service';

/**
 * Downloads `GET /complaints/export` for whatever filter the screen is currently showing.
 * SPEC.md §14 F14.
 *
 * **It sends the caller's active filter and nothing else.** The filter comes in as an input
 * from the screen that owns it, goes through the same `ComplaintService.toParams` the list
 * uses, and the server applies the same scope and filter code to both. So "export what I am
 * looking at" is true by construction rather than by two implementations agreeing.
 *
 * A blob and a synthetic anchor click, not an `<a href>`: the endpoint needs the bearer
 * token and an anchor cannot carry one.
 */
@Component({
  selector: 'app-export-button',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule],
  template: `
    <button matButton [disabled]="download.isLoading()" (click)="onExport()">
      <mat-icon>download</mat-icon>
      {{ download.isLoading() ? 'Preparing…' : 'Export CSV' }}
    </button>
  `,
})
export class ExportButton {
  private readonly complaints = inject(ComplaintService);

  /** The filter the screen is showing. Passed straight through, never interpreted here. */
  readonly filter = input<ComplaintListParams>({});

  /** Zero keeps the resource idle; bumping it runs exactly one download. */
  private readonly requested = signal(0);

  /**
   * A resource rather than a bare `.subscribe()`, so teardown is handled if the user
   * navigates mid-download (`.claude/rules/frontend-angular.md`). Errors reach the snackbar
   * through the interceptor chain; nothing is caught here.
   */
  protected readonly download = rxResource({
    params: () =>
      this.requested() === 0 ? undefined : { n: this.requested(), filter: this.filter() },
    stream: ({ params }) => this.complaints.export(params.filter).pipe(map(save)),
  });

  protected onExport(): void {
    this.requested.update((n) => n + 1);
  }
}

/**
 * Hands the blob to the browser. The filename comes from the server's
 * `Content-Disposition`, but a blob URL has no access to response headers, so this repeats
 * the server's naming rule — the one place the client duplicates something, and it is a
 * cosmetic string rather than a decision.
 */
function save(blob: Blob): number {
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement('a');

  anchor.href = url;
  anchor.download = `complaints-${new Date().toISOString().slice(0, 10)}.csv`;
  anchor.click();

  // Revoked on the next tick: revoking synchronously races the download in Safari.
  setTimeout(() => URL.revokeObjectURL(url));

  return blob.size;
}
