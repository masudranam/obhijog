import { Component, inject, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
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
  private readonly snackBar = inject(MatSnackBar);

  /** Bumped to make `rxResource` refetch after a manual sweep. */
  private readonly reload = signal(0);

  protected readonly sweeping = signal(false);

  protected readonly columns = [
    'reference',
    'title',
    'priority',
    'status',
    'assignee',
    'level',
    'overdue',
  ];

  protected readonly result = rxResource({
    params: () => ({ reload: this.reload() }),
    stream: () => this.sla.breaches(),
  });

  /**
   * Runs one pass and reloads. Errors are surfaced by the interceptor chain, so there is no
   * `catchError` here — swallowing a failure into an unchanged list would be worse than the
   * error (`.claude/rules/frontend-angular.md`).
   */
  protected sweep(): void {
    this.sweeping.set(true);

    this.sla.sweep().subscribe({
      next: (result) => {
        this.sweeping.set(false);
        this.reload.update((n) => n + 1);

        this.snackBar.open(
          `Swept ${result.examined} complaint${result.examined === 1 ? '' : 's'}: ` +
            `${result.warned} warned, ${result.breached} breached, ` +
            `${result.escalatedLevel2} escalated, ${result.autoClosed} auto-closed.`,
          'Dismiss',
          { duration: 6000 },
        );
      },
      error: () => this.sweeping.set(false),
    });
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
