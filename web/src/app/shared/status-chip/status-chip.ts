import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { ComplaintStatus } from '../../core/models/api';

/**
 * The status, as a coloured chip. SPEC.md §15.
 *
 * Colour is presentation, not policy: this maps a status the server already decided to a
 * palette entry. It never decides what the status should be, and it holds no copy of the
 * guard table.
 */
@Component({
  selector: 'app-status-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="chip" [class]="tone()">{{ label() }}</span>`,
  styles: `
    .chip {
      display: inline-block;
      padding: 2px 10px;
      border-radius: 999px;
      font-size: 12px;
      font-weight: 600;
      line-height: 20px;
      white-space: nowrap;
    }
    .new {
      background: #e3f2fd;
      color: #0d47a1;
    }
    .assigned {
      background: #ede7f6;
      color: #4527a0;
    }
    .in-progress {
      background: #fff8e1;
      color: #e65100;
    }
    .resolved {
      background: #e8f5e9;
      color: #1b5e20;
    }
    .closed {
      background: #eceff1;
      color: #37474f;
    }
    .rejected {
      background: #ffebee;
      color: #b71c1c;
    }
  `,
})
export class StatusChip {
  readonly status = input.required<ComplaintStatus>();

  protected readonly tone = computed(() =>
    this.status()
      .replace(/([a-z])([A-Z])/g, '$1-$2')
      .toLowerCase(),
  );

  /** `InProgress` reads badly in a UI; everything else is already a word. */
  protected readonly label = computed(() =>
    this.status() === 'InProgress' ? 'In progress' : this.status(),
  );
}
