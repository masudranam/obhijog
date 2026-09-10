import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

/**
 * The SLA state, as a badge. SPEC.md §15, §11.2.
 *
 * **This component decides nothing.** Whether a complaint is warning or breached is server
 * policy, recorded in `slaWarnedAt` and `slaBreachedAt` by the sweeper against the
 * 80/100/150 thresholds of §11.2. Reproducing those thresholds here would be a second copy
 * that drifts, and is a review finding.
 *
 * What it may do is arithmetic on values it was given: the countdown to `slaDueAt` is
 * display, not policy.
 */
@Component({
  selector: 'app-sla-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <span class="badge" [class]="tone()" [title]="tooltip()">
      {{ text() }}
      @if (escalationLevel() > 0) {
        <span class="level">L{{ escalationLevel() }}</span>
      }
    </span>
  `,
  styles: `
    .badge {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 2px 10px;
      border-radius: 999px;
      font-size: 12px;
      font-weight: 600;
      line-height: 20px;
      white-space: nowrap;
    }
    .ok {
      background: #e8f5e9;
      color: #1b5e20;
    }
    .warning {
      background: #fff8e1;
      color: #e65100;
    }
    .breached {
      background: #ffebee;
      color: #b71c1c;
    }
    .met {
      background: #eceff1;
      color: #37474f;
    }
    .level {
      background: rgba(0, 0, 0, 0.12);
      border-radius: 4px;
      padding: 0 4px;
      font-size: 11px;
    }
  `,
})
export class SlaBadge {
  readonly slaDueAt = input.required<string>();
  readonly slaWarnedAt = input<string | null>(null);
  readonly slaBreachedAt = input<string | null>(null);
  readonly resolvedAt = input<string | null>(null);
  readonly escalationLevel = input<number>(0);

  /**
   * A breach is historical and survives resolution (§13.3), so it is checked before
   * resolution — a complaint resolved late shows as breached, not as met.
   */
  protected readonly tone = computed(() => {
    if (this.slaBreachedAt()) {
      return 'breached';
    }
    if (this.resolvedAt()) {
      return 'met';
    }
    return this.slaWarnedAt() ? 'warning' : 'ok';
  });

  protected readonly text = computed(() => {
    switch (this.tone()) {
      case 'breached':
        return 'SLA breached';
      case 'met':
        return 'Resolved in time';
      case 'warning':
        return `Due ${this.countdown()}`;
      default:
        return `Due ${this.countdown()}`;
    }
  });

  protected readonly tooltip = computed(() => `Due ${new Date(this.slaDueAt()).toLocaleString()}`);

  /** Display arithmetic over a server-supplied instant. */
  private readonly countdown = computed(() => {
    const remainingMs = new Date(this.slaDueAt()).getTime() - Date.now();
    const overdue = remainingMs < 0;
    const minutes = Math.floor(Math.abs(remainingMs) / 60_000);
    const days = Math.floor(minutes / 1440);
    const hours = Math.floor((minutes % 1440) / 60);

    const span =
      days > 0 ? `${days}d ${hours}h` : hours > 0 ? `${hours}h ${minutes % 60}m` : `${minutes}m`;

    return overdue ? `${span} ago` : `in ${span}`;
  });
}
