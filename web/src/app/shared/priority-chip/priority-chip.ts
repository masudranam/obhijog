import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

import { ComplaintPriority } from '../../core/models/api';

/**
 * Priority, as a chip. SPEC.md §15.
 *
 * Like {@link StatusChip} this maps a value the server decided to a palette entry and
 * decides nothing itself. Priority is seeded from the category and edited only by a Dept
 * Admin through `PUT /complaints/{id}/priority` (§12.4).
 */
@Component({
  selector: 'app-priority-chip',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<span class="chip" [class]="tone()">{{ priority() }}</span>`,
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
    .low {
      background: #eceff1;
      color: #455a64;
    }
    .normal {
      background: #e3f2fd;
      color: #0d47a1;
    }
    .high {
      background: #fff3e0;
      color: #e65100;
    }
    .critical {
      background: #ffebee;
      color: #b71c1c;
    }
  `,
})
export class PriorityChip {
  readonly priority = input.required<ComplaintPriority>();

  protected readonly tone = computed(() => this.priority().toLowerCase());
}
