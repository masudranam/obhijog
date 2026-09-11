import { DatePipe } from '@angular/common';
import { Component, computed, inject, input, linkedSignal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatDividerModule } from '@angular/material/divider';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { RouterLink } from '@angular/router';
import { of } from 'rxjs';

import { AuthService } from '../../../core/auth/auth.service';
import { AttachmentService } from '../../../core/complaints/attachment.service';
import { ComplaintService } from '../../../core/complaints/complaint.service';
import { ComplaintAction, ComplaintDetail, ComplaintPriority } from '../../../core/models/api';
import { AttachmentGallery } from '../../../shared/attachment-gallery/attachment-gallery';
import { CommentThread } from '../../../shared/comment-thread/comment-thread';
import { ComplaintActions } from '../../../shared/complaint-actions/complaint-actions';
import { MapPicker } from '../../../shared/map-picker/map-picker';
import { PriorityChip } from '../../../shared/priority-chip/priority-chip';
import { SlaBadge } from '../../../shared/sla-badge/sla-badge';
import { StatusChip } from '../../../shared/status-chip/status-chip';

/** §12.4 — the four values a Dept Admin may set. Not a transition; nothing moves. */
const PRIORITIES: ComplaintPriority[] = ['Low', 'Normal', 'High', 'Critical'];

/**
 * One complaint, with its timeline, its actions and its comments. F5, F7, F9.
 *
 * Shared by all three roles: what differs between them is what the *server* returns —
 * `availableActions`, and which comments are visible. There is no role branch here beyond
 * where the back link points and whether to draw a control the server would refuse anyway.
 *
 * A complaint the caller may not see returns 404, so this screen shows "not found" for both
 * cases without knowing which it was — that is the point of §9.2.
 */
@Component({
  selector: 'app-complaint-detail',
  imports: [
    DatePipe,
    MatButtonModule,
    MatCardModule,
    MatDividerModule,
    MatFormFieldModule,
    MatIconModule,
    MatProgressBarModule,
    MatSelectModule,
    RouterLink,
    AttachmentGallery,
    CommentThread,
    ComplaintActions,
    MapPicker,
    PriorityChip,
    SlaBadge,
    StatusChip,
  ],
  templateUrl: './complaint-detail.html',
  styleUrl: './complaint-detail.css',
})
export class ComplaintDetailPage {
  private readonly complaints = inject(ComplaintService);
  private readonly attachmentService = inject(AttachmentService);
  private readonly auth = inject(AuthService);

  /** Bound from the route by `withComponentInputBinding`. */
  readonly id = input.required<string>();

  protected readonly priorities = PRIORITIES;
  protected readonly me = this.auth.me;

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

  /**
   * The complaint as currently known: the loaded one, or the newer copy a transition
   * returned. Actions come back with their own `availableActions`, so re-rendering from
   * the response avoids a second round trip and cannot disagree with the server.
   *
   * `linkedSignal` rather than `signal`, so navigating to a different complaint discards
   * the previous one's applied result instead of showing it against the new id.
   */
  protected readonly complaint = linkedSignal<ComplaintDetail | undefined, ComplaintDetail | null>({
    source: () => this.result.value(),
    computation: (loaded) => loaded ?? null,
  });

  protected readonly isCitizen = computed(() => this.me()?.role === 'Citizen');
  protected readonly isDeptAdmin = computed(() => this.me()?.role === 'DeptAdmin');

  /** Where "back" goes. Navigation, not permission. */
  protected readonly backTo = computed(() => {
    switch (this.me()?.role) {
      case 'Staff':
        return '/queue';
      case 'DeptAdmin':
        return '/inbox';
      default:
        return '/my';
    }
  });

  /**
   * The assignee picker's options. Only a Dept Admin may read a department's staff, so for
   * everyone else this stays empty — and for everyone else `availableActions` contains no
   * action that needs it.
   */
  protected readonly staff = rxResource({
    params: () => ({ departmentId: this.complaint()?.departmentId, admin: this.isDeptAdmin() }),
    stream: ({ params }) =>
      params.admin && params.departmentId
        ? this.complaints.departmentStaff(params.departmentId)
        : of([]),
    defaultValue: [],
  });

  protected readonly categories = rxResource({
    params: () => ({ admin: this.isDeptAdmin() }),
    stream: ({ params }) => (params.admin ? this.complaints.categories() : of([])),
    defaultValue: [],
  });

  /**
   * F9: the timeline is history rows and escalation events, merged chronologically. Two
   * lists on the wire and one on screen — the API does not invent a union type, and the
   * order is the only thing the client decides.
   */
  protected readonly timeline = computed<TimelineEntry[]>(() => {
    const complaint = this.complaint();

    if (!complaint) {
      return [];
    }

    const entries: TimelineEntry[] = [
      ...complaint.history.map((h) => ({
        key: h.id,
        at: h.changedAt,
        what: describe(h.action, h.changedByName, h.isSystem),
        note: h.note,
        escalation: false,
      })),
      ...complaint.escalations.map((e) => ({
        key: e.id,
        at: e.raisedAt,
        what: 'Escalated to level ' + e.level,
        note: null,
        escalation: true,
      })),
    ];

    return entries.sort((a, b) => a.at.localeCompare(b.at));
  });

  protected onChanged(updated: ComplaintDetail): void {
    this.complaint.set(updated);
  }

  protected onPriority(priority: ComplaintPriority): void {
    if (priority === this.complaint()?.priority) {
      return;
    }

    this.complaints
      .changePriority(this.id(), { priority })
      .subscribe((updated) => this.complaint.set(updated));
  }
}

interface TimelineEntry {
  key: string;
  at: string;
  what: string;
  note: string | null;
  escalation: boolean;
}

/** The wording of a history row. Per action, not per status — no guard table here. */
function describe(action: ComplaintAction, by: string | null, isSystem: boolean): string {
  const actor = isSystem ? 'The system' : (by ?? 'Someone');

  switch (action) {
    case 'Submit':
      return actor + ' reported this';
    case 'Assign':
      return actor + ' assigned it';
    case 'Reassign':
      return actor + ' reassigned it';
    case 'Recategorize':
      return actor + ' recategorized it';
    case 'Start':
      return actor + ' started work';
    case 'Resolve':
      return actor + ' marked it resolved';
    case 'Close':
      return actor + ' closed it';
    case 'Reopen':
      return actor + ' reopened it';
    case 'Reject':
      return actor + ' rejected it';
    case 'Priority':
      return actor + ' changed the priority';
    default:
      return actor + ' updated it';
  }
}
