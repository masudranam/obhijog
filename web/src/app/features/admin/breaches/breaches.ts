import { Component, computed, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { MatTooltipModule } from '@angular/material/tooltip';
import { RouterLink } from '@angular/router';

import { SlaService } from '../../../core/sla/sla.service';
import { PriorityChip } from '../../../shared/priority-chip/priority-chip';
import { StatusChip } from '../../../shared/status-chip/status-chip';

/**
 * `GET /sla/breaches`. SPEC.md §14 F12.
 *
 * **Nothing on this screen decides what a breach is.** The list is what the server returned,
 * every row carries the escalation level and hours-overdue the server computed, and the
 * 80/100/150 thresholds of §11.2 appear nowhere in this file (§15).
 *
 * The manual sweep button is here rather than on a settings page because this is the screen
 * where someone would want it: run a pass, see the list change.
 */
@Component({
  selector: 'app-breaches',
  imports: [
    MatButtonModule,
    MatCardModule,
    MatIconModule,
    MatProgressBarModule,
    MatTableModule,
    MatTooltipModule,
    RouterLink,
    PriorityChip,
    StatusChip,
  ],
  templateUrl: './breaches.html',
  styleUrl: './breaches.css',
})
export class Breaches {
  private readonly sla = inject(SlaService);

  /**
   * Zero until the button is pressed, which keeps {@link sweep} idle: `rxResource` does not
   * request anything while its params are `undefined`. Bumping it triggers exactly one pass.
   */
  private readonly requested = signal(0);

  protected readonly columns = [
    'reference',
    'title',
    'priority',
    'status',
    'assignee',
    'level',
    'overdue',
  ];

  /**
   * A resource rather than a `.subscribe()`, so teardown is handled and a navigation
   * mid-request cannot leave a callback holding this component
   * (`.claude/rules/frontend-angular.md`). Errors reach the snackbar through the
   * interceptor chain; nothing is caught here.
   */
  protected readonly sweep = rxResource({
    params: () => (this.requested() === 0 ? undefined : this.requested()),
    stream: () => this.sla.sweep(),
  });

  /**
   * Re-reads whenever a sweep completes: the params signal reads `sweep.value()`, so a new
   * counters object is a new request. No manual refresh call, and no chance of the list and
   * the summary disagreeing about which pass they describe.
   */
  protected readonly result = rxResource({
    params: () => ({ after: this.sweep.value() }),
    stream: () => this.sla.breaches(),
  });

  protected readonly busy = computed(() => this.sweep.isLoading() || this.result.isLoading());

  protected onSweep(): void {
    this.requested.update((n) => n + 1);
  }

  /** Display formatting of a server-supplied number. Not a threshold. */
  protected overdue(hours: number): string {
    if (hours < 24) {
      return `${Math.round(hours)}h`;
    }

    const days = Math.floor(hours / 24);

    return `${days}d ${Math.round(hours - days * 24)}h`;
  }
}
