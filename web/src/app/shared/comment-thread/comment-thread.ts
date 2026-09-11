import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { ComplaintService } from '../../core/complaints/complaint.service';

/**
 * Comments on a complaint. SPEC.md §9.4, F9.
 *
 * The internal-comment rule is **not** enforced here. A Citizen's `GET .../comments` never
 * returns an internal row — the filter is applied in the query on the server, so there is
 * nothing for this component to hide. `canWriteInternal` only decides whether to draw the
 * checkbox; a Citizen who posts `isInternal` anyway gets a `403`.
 */
@Component({
  selector: 'app-comment-thread',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    FormsModule,
    MatButtonModule,
    MatCheckboxModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
  ],
  template: `
    @if (thread.isLoading()) {
      <mat-progress-bar mode="indeterminate" />
    }

    <ol class="thread">
      @for (comment of thread.value(); track comment.id) {
        <li [class.internal]="comment.isInternal">
          <div class="meta">
            <strong>{{ comment.authorName }}</strong>
            <span class="when">{{ comment.createdAt | date: 'medium' }}</span>
            @if (comment.isInternal) {
              <span class="tag">Internal</span>
            }
          </div>
          <p class="body">{{ comment.body }}</p>
        </li>
      } @empty {
        @if (!thread.isLoading()) {
          <li class="empty">No comments yet.</li>
        }
      }
    </ol>

    <form class="composer" (ngSubmit)="post()">
      <mat-form-field appearance="outline" class="full">
        <mat-label>Add a comment</mat-label>
        <textarea
          matInput
          rows="3"
          maxlength="2000"
          name="body"
          [ngModel]="draft()"
          (ngModelChange)="draft.set($event)"
        ></textarea>
      </mat-form-field>

      <div class="composer-actions">
        @if (canWriteInternal()) {
          <mat-checkbox
            name="internal"
            [ngModel]="internal()"
            (ngModelChange)="internal.set($event)"
          >
            Internal — not visible to the reporter
          </mat-checkbox>
        }
        <button matButton="filled" type="submit" [disabled]="busy() || !draft().trim()">
          Post
        </button>
      </div>
    </form>
  `,
  styles: `
    .thread {
      list-style: none;
      margin: 0 0 16px;
      padding: 0;
      display: flex;
      flex-direction: column;
      gap: 12px;
    }
    .thread li {
      border-left: 3px solid #e0e0e0;
      padding: 4px 0 4px 12px;
    }
    .thread li.internal {
      border-left-color: #ff8f00;
      background: #fff8e1;
      border-radius: 0 4px 4px 0;
      padding-right: 12px;
    }
    .meta {
      display: flex;
      align-items: center;
      gap: 8px;
      font-size: 13px;
    }
    .when {
      color: rgba(0, 0, 0, 0.6);
    }
    .tag {
      font-size: 11px;
      font-weight: 600;
      text-transform: uppercase;
      color: #e65100;
    }
    .body {
      margin: 4px 0 0;
      white-space: pre-wrap;
    }
    .empty {
      border-left: none;
      padding-left: 0;
      color: rgba(0, 0, 0, 0.6);
    }
    .full {
      width: 100%;
    }
    .composer-actions {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 16px;
    }
  `,
})
export class CommentThread {
  private readonly complaints = inject(ComplaintService);

  readonly complaintId = input.required<string>();

  /** Whether to offer the internal checkbox. Staff and Dept Admin only (§9.4). */
  readonly canWriteInternal = input(false);

  protected readonly draft = signal('');
  protected readonly internal = signal(false);
  protected readonly busy = signal(false);

  protected readonly thread = rxResource({
    params: () => ({ id: this.complaintId() }),
    stream: ({ params }) => this.complaints.comments(params.id),
    defaultValue: [],
  });

  protected post(): void {
    const body = this.draft().trim();

    if (!body || this.busy()) {
      return;
    }

    this.busy.set(true);

    this.complaints.addComment(this.complaintId(), { body, isInternal: this.internal() }).subscribe({
      next: () => {
        this.busy.set(false);
        this.draft.set('');
        this.internal.set(false);
        this.thread.reload();
      },
      error: () => this.busy.set(false),
    });
  }
}
