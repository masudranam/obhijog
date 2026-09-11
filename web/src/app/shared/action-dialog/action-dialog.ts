import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';

import { Category, StaffMember, TransitionAction, TransitionRequest } from '../../core/models/api';

/**
 * What a transition needs beyond the action itself.
 *
 * This is a **form definition**, not a second copy of the guard table. The guard table
 * answers "may this caller do this from this status", and that answer arrives on the wire
 * as `availableActions` — this file never asks it. What it says is only which input to put
 * on screen for an action the server already declared available, keyed on the action alone
 * and never on a status or a role.
 *
 * The server validates the payload regardless (§12.3's required-payload column), so a wrong
 * entry here produces a `400`, not a bad write.
 */
const FORMS: Record<TransitionAction, ActionForm> = {
  Assign: { verb: 'Assign', field: 'assignee', confirm: 'Assign' },
  Reassign: { verb: 'Reassign', field: 'assignee', confirm: 'Reassign' },
  Recategorize: { verb: 'Recategorize', field: 'category', confirm: 'Move' },
  Reject: { verb: 'Reject', field: 'note', noteLabel: 'Why this is being rejected', confirm: 'Reject' },
  Start: { verb: 'Start work', field: 'none', confirm: 'Start' },
  Resolve: { verb: 'Resolve', field: 'note', noteLabel: 'What was done', confirm: 'Resolve' },
  Close: { verb: 'Close', field: 'none', confirm: 'Close' },
  Reopen: { verb: 'Reopen', field: 'note', noteLabel: 'Why this is being reopened', confirm: 'Reopen' },
};

interface ActionForm {
  verb: string;
  field: 'none' | 'note' | 'assignee' | 'category';
  noteLabel?: string;
  confirm: string;
}

export interface ActionDialogData {
  action: TransitionAction;
  title: string;
  /** Loaded by the caller; empty when the caller is not a Dept Admin and cannot assign. */
  staff: StaffMember[];
  categories: Category[];
}

@Component({
  selector: 'app-action-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    MatButtonModule,
    MatDialogModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
  ],
  template: `
    <h2 mat-dialog-title>{{ form().verb }}</h2>

    <mat-dialog-content>
      <p class="subject">{{ data.title }}</p>

      @switch (form().field) {
        @case ('note') {
          <mat-form-field appearance="outline" class="full">
            <mat-label>{{ form().noteLabel }}</mat-label>
            <textarea
              matInput
              rows="4"
              maxlength="1000"
              [ngModel]="note()"
              (ngModelChange)="note.set($event)"
            ></textarea>
            <mat-hint>Required. This is visible to the person who reported it.</mat-hint>
          </mat-form-field>
        }
        @case ('assignee') {
          <mat-form-field appearance="outline" class="full">
            <mat-label>Assign to</mat-label>
            <mat-select [ngModel]="assigneeId()" (ngModelChange)="assigneeId.set($event)">
              @for (member of data.staff; track member.id) {
                <mat-option [value]="member.id">{{ member.fullName }}</mat-option>
              }
            </mat-select>
            @if (!data.staff.length) {
              <mat-hint>No active staff in this department.</mat-hint>
            }
          </mat-form-field>
        }
        @case ('category') {
          <mat-form-field appearance="outline" class="full">
            <mat-label>New category</mat-label>
            <mat-select [ngModel]="categoryId()" (ngModelChange)="categoryId.set($event)">
              @for (category of data.categories; track category.id) {
                <mat-option [value]="category.id">
                  {{ category.name }} — {{ category.departmentName }}
                </mat-option>
              }
            </mat-select>
            <mat-hint>
              Recategorizing clears the assignee and recomputes the deadline from the original
              report time.
            </mat-hint>
          </mat-form-field>
        }
        @default {
          <p class="muted">This cannot be undone from here.</p>
        }
      }
    </mat-dialog-content>

    <mat-dialog-actions align="end">
      <button matButton mat-dialog-close>Cancel</button>
      <button matButton="filled" [disabled]="!ready()" (click)="submit()">
        {{ form().confirm }}
      </button>
    </mat-dialog-actions>
  `,
  styles: `
    .subject {
      margin: 0 0 16px;
      font-weight: 500;
    }
    .full {
      width: 100%;
      min-width: 360px;
    }
    .muted {
      color: rgba(0, 0, 0, 0.6);
    }
  `,
})
export class ActionDialog {
  protected readonly data = inject<ActionDialogData>(MAT_DIALOG_DATA);
  private readonly dialogRef = inject(MatDialogRef<ActionDialog, TransitionRequest>);

  protected readonly note = signal('');
  protected readonly assigneeId = signal<string | null>(null);
  protected readonly categoryId = signal<string | null>(null);

  protected readonly form = computed(() => FORMS[this.data.action]);

  protected readonly ready = computed(() => {
    switch (this.form().field) {
      case 'note':
        return this.note().trim().length > 0;
      case 'assignee':
        return this.assigneeId() !== null;
      case 'category':
        return this.categoryId() !== null;
      default:
        return true;
    }
  });

  protected submit(): void {
    this.dialogRef.close({
      action: this.data.action,
      note: this.note().trim() || null,
      assigneeId: this.assigneeId(),
      categoryId: this.categoryId(),
    });
  }
}
