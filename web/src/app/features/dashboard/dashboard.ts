import { Component, computed, inject } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { RouterLink } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';
import { DashboardService } from '../../core/dashboard/dashboard.service';
import { ComplaintStatus } from '../../core/models/api';

/** One figure, and where clicking it goes. */
interface Tile {
  readonly label: string;
  readonly value: string;
  readonly hint: string;
  readonly tone: 'plain' | 'warning' | 'breached';
  readonly link?: readonly [string, Record<string, string | string[]>];
}

/**
 * `GET /dashboard/summary` as a screen. SPEC.md §14 F13.
 *
 * **Every number here was computed by the server.** This component formats and arranges; it
 * does not count, does not decide what a warning is, and holds no copy of the 80/100/150
 * thresholds (§15, CLAUDE.md non-negotiable 6). The one piece of arithmetic it does is
 * rounding a figure for display.
 *
 * The same screen serves all three roles, because the summary arrives already scoped — a
 * Citizen sees their own complaints counted, a Dept Admin their department's (§9.3). There
 * is no role branch in this file beyond the wording of a caption.
 */
@Component({
  selector: 'app-dashboard',
  imports: [MatCardModule, MatIconModule, MatProgressBarModule, RouterLink],
  templateUrl: './dashboard.html',
  styleUrl: './dashboard.css',
})
export class Dashboard {
  private readonly dashboard = inject(DashboardService);
  private readonly auth = inject(AuthService);

  protected readonly result = rxResource({
    params: () => ({}),
    stream: () => this.dashboard.summary(),
  });

  protected readonly isCitizen = computed(() => this.auth.role() === 'Citizen');

  /** Where a tile links to. Citizens browse `/my`; everyone else the department list. */
  private readonly listPath = computed(() => (this.isCitizen() ? '/my' : '/inbox'));

  protected readonly caption = computed(() =>
    this.isCitizen()
      ? 'Your complaints, and how they are tracking against their deadlines.'
      : "Your department's complaints, and how they are tracking against their deadlines.",
  );

  /**
   * The headline figures. Ordered by what someone opening this screen needs first: what is
   * already late, then what is about to be, then the backlog.
   */
  protected readonly tiles = computed<Tile[]>(() => {
    const summary = this.result.value();

    if (!summary) {
      return [];
    }

    return [
      {
        label: 'Breached',
        value: `${summary.breachedOpen}`,
        hint: 'Open and past the deadline',
        tone: summary.breachedOpen > 0 ? 'breached' : 'plain',
        link: [this.listPath(), { slaState: 'Breached' }],
      },
      {
        label: 'Approaching',
        value: `${summary.warningOpen}`,
        hint: 'Open and inside the warning band',
        tone: summary.warningOpen > 0 ? 'warning' : 'plain',
        link: [this.listPath(), { slaState: 'Warning' }],
      },
      {
        label: 'Due in 24 hours',
        value: `${summary.dueNext24h}`,
        hint: 'Open, deadline within a day',
        tone: 'plain',
      },
      {
        label: 'Open',
        value: `${summary.totalOpen}`,
        hint: 'New, assigned or in progress',
        tone: 'plain',
        link: [this.listPath(), { status: OPEN_STATUSES as unknown as string[] }],
      },
    ];
  });

  /** The escalation ladder's two rungs, shown together because they are read together. */
  protected readonly escalations = computed(() => {
    const summary = this.result.value();

    return summary
      ? [
          { level: 1, count: summary.escalatedLevel1 },
          { level: 2, count: summary.escalatedLevel2 },
        ]
      : [];
  });

  /** All six statuses, in lifecycle order rather than whatever order the object arrived in. */
  protected readonly byStatus = computed(() => {
    const summary = this.result.value();

    return summary ? STATUS_ORDER.map((status) => ({ status, count: summary.byStatus[status] })) : [];
  });

  /**
   * Null is not zero. "Nothing was resolved this month" and "everything took no time and
   * none of it met its SLA" are different statements, and the server distinguishes them.
   */
  protected readonly resolution = computed(() => {
    const summary = this.result.value();

    if (!summary || summary.resolvedLast30Days === 0) {
      return null;
    }

    return {
      count: summary.resolvedLast30Days,
      averageHours: summary.avgResolutionHours,
      compliance: summary.slaCompliancePct,
    };
  });
}

const OPEN_STATUSES: ComplaintStatus[] = ['New', 'Assigned', 'InProgress'];

const STATUS_ORDER: ComplaintStatus[] = [
  'New',
  'Assigned',
  'InProgress',
  'Resolved',
  'Closed',
  'Rejected',
];
