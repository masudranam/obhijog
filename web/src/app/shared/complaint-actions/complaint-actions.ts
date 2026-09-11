import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatDialog } from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';

import { ComplaintService } from '../../core/complaints/complaint.service';
import { ComplaintDetail, TransitionAction } from '../../core/models/api';
import { ActionDialog, ActionDialogData } from '../action-dialog/action-dialog';

/** Icon and wording per action. Presentation only — see the note in `action-dialog`. */
const LABELS: Record<TransitionAction, { icon: string; label: string }> = {
  Assign: { icon: 'person_add', label: 'Assign' },
  Reassign: { icon: 'swap_horiz', label: 'Reassign' },
  Recategorize: { icon: 'move_down', label: 'Recategorize' },
  Reject: { icon: 'block', label: 'Reject' },
  Start: { icon: 'play_arrow', label: 'Start work' },
  Resolve: { icon: 'task_alt', label: 'Resolve' },
  Close: { icon: 'check_circle', label: 'Close' },
  Reopen: { icon: 'restart_alt', label: 'Reopen' },
};

/**
 * The action bar. SPEC.md §15, F7.
 *
 * **Every button here comes from `availableActions`.** There is no `switch (status)`, no
 * role check and no `canAssign()` — the guard table lives in `Obhijog.Domain` and the
 * server already applied it. That is the whole point: a button can never appear for an
 * action the write path would then refuse, because both read the same twelve rows.
 *
 * The UI hiding a control is cosmetic. The server-side guard is the security, and the
 * backend tests prove it (§21.5).
 */
@Component({
  selector: 'app-complaint-actions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule],
  template: `
    @if (complaint().availableActions.length) {
      <div class="actions">
        @for (action of complaint().availableActions; track action) {
          <button
            matButton="outlined"
            [disabled]="busy()"
            (click)="run(action)"
            [attr.data-action]="action"
          >
            <mat-icon>{{ label(action).icon }}</mat-icon>
            {{ label(action).label }}
          </button>
        }
      </div>
    } @else {
      <p class="muted">No actions are available to you on this complaint.</p>
    }
  `,
  styles: `
    .actions {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
    }
    .muted {
      margin: 0;
      color: rgba(0, 0, 0, 0.6);
      font-size: 14px;
    }
  `,
})
export class ComplaintActions {
  private readonly complaints = inject(ComplaintService);
  private readonly dialog = inject(MatDialog);
  private readonly snackBar = inject(MatSnackBar);

  readonly complaint = input.required<ComplaintDetail>();

  /**
   * Staff of the complaint's department, for the assignee picker. Only a Dept Admin may
   * read them, so the parent loads them and passes an empty list otherwise.
   */
  readonly staff = input<ActionDialogData['staff']>([]);
  readonly categories = input<ActionDialogData['categories']>([]);

  /** Emits the complaint as the server returned it — including its new availableActions. */
  readonly changed = output<ComplaintDetail>();

  protected readonly busy = signal(false);

  protected label(action: TransitionAction) {
    return LABELS[action];
  }

  protected run(action: TransitionAction): void {
    const data: ActionDialogData = {
      action,
      title: this.complaint().title,
      staff: this.staff(),
      categories: this.categories(),
    };

    this.dialog
      .open(ActionDialog, { data, autoFocus: 'dialog' })
      .afterClosed()
      .subscribe((request) => {
        if (!request) {
          return;
        }

        this.busy.set(true);

        // The error interceptor surfaces a failure; this only has to stop spinning. A
        // 409 here is real and expected — someone else moved the complaint first.
        this.complaints.transition(this.complaint().id, request).subscribe({
          next: (updated) => {
            this.busy.set(false);
            this.snackBar.open(LABELS[action].label + ' — done.', 'Dismiss', { duration: 4000 });
            this.changed.emit(updated);
          },
          error: () => this.busy.set(false),
        });
      });
  }
}
