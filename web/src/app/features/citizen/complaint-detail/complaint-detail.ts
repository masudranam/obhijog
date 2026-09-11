import { DatePipe } from '@angular/common';
import { Component, inject, input } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import { AttachmentService } from '../../../core/complaints/attachment.service';
import { ComplaintService } from '../../../core/complaints/complaint.service';
import { ComplaintAction } from '../../../core/models/api';
import { AttachmentGallery } from '../../../shared/attachment-gallery/attachment-gallery';
import { MapPicker } from '../../../shared/map-picker/map-picker';
import { SlaBadge } from '../../../shared/sla-badge/sla-badge';
import { StatusChip } from '../../../shared/status-chip/status-chip';

/**
 * F5 — one complaint, with its timeline.
 *
 * No action buttons: those render from `availableActions`, which the API does not send
 * until the guard table exists in M6 (§15). Inventing them here from the status would be
 * a client-side copy of the guard table, which is exactly the thing §15 forbids.
 *
 * A complaint the caller may not see returns 404 from the server, so this screen shows
 * "not found" for both cases without knowing which it was — that is the point of §9.2.
 */
@Component({
  selector: 'app-complaint-detail',
  imports: [
    DatePipe,
    MatButtonModule,
    MatCardModule,
    MatDividerModule,
    MatIconModule,
    MatProgressBarModule,
    RouterLink,
    AttachmentGallery,
    MapPicker,
    SlaBadge,
    StatusChip,
  ],
  templateUrl: './complaint-detail.html',
  styleUrl: './complaint-detail.css',
})
export class ComplaintDetailPage {
  private readonly complaints = inject(ComplaintService);
  private readonly attachmentService = inject(AttachmentService);

  /** Bound from the route by `withComponentInputBinding`. */
  readonly id = input.required<string>();

  protected readonly result = rxResource({
    params: () => ({ id: this.id() }),
    stream: ({ params }) => this.complaints.get(params.id),
  });

  /**
   * A separate request from the complaint, because the read URLs are short-lived SAS
   * tokens: folding them into the detail DTO would tie their lifetime to a payload the
   * client may hold on screen for an hour.
   */
  protected readonly attachments = rxResource({
    params: () => ({ id: this.id() }),
    stream: ({ params }) => this.attachmentService.list(params.id),
    defaultValue: [],
  });

  /** History rows are the timeline; the wording is per action, not per status. */
  protected describe(action: ComplaintAction, by: string | null, isSystem: boolean): string {
    const actor = isSystem ? 'The system' : (by ?? 'Someone');

    switch (action) {
      case 'Submit':
        return `${actor} reported this`;
      case 'Assign':
        return `${actor} assigned it`;
      case 'Reassign':
        return `${actor} reassigned it`;
      case 'Recategorize':
        return `${actor} recategorized it`;
      case 'Start':
        return `${actor} started work`;
      case 'Resolve':
        return `${actor} marked it resolved`;
      case 'Close':
        return `${actor} closed it`;
      case 'Reopen':
        return `${actor} reopened it`;
      case 'Reject':
        return `${actor} rejected it`;
      default:
        return `${actor} updated it`;
    }
  }
}
