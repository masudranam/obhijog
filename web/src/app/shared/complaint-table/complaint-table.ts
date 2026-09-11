import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MatPaginatorModule, PageEvent } from '@angular/material/paginator';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';

import { ComplaintListItem, Page } from '../../core/models/api';
import { PriorityChip } from '../priority-chip/priority-chip';
import { SlaBadge } from '../sla-badge/sla-badge';
import { StatusChip } from '../status-chip/status-chip';

/**
 * The work list, shared by the staff queue and the department inbox. Purely presentational:
 * it renders a page the server already scoped, filtered and sorted (§9.3, §13.3), and every
 * badge on it renders from a field the server sent.
 */
@Component({
  selector: 'app-complaint-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    DatePipe,
    MatPaginatorModule,
    MatTableModule,
    RouterLink,
    PriorityChip,
    SlaBadge,
    StatusChip,
  ],
  template: `
    <table mat-table [dataSource]="page().items" class="full">
      <ng-container matColumnDef="reference">
        <th mat-header-cell *matHeaderCellDef>Reference</th>
        <td mat-cell *matCellDef="let row">
          <a [routerLink]="['/complaints', row.id]" class="ref">{{ row.referenceNumber }}</a>
        </td>
      </ng-container>

      <ng-container matColumnDef="title">
        <th mat-header-cell *matHeaderCellDef>Problem</th>
        <td mat-cell *matCellDef="let row">
          <div class="title">{{ row.title }}</div>
          <div class="meta">{{ row.categoryName }}</div>
        </td>
      </ng-container>

      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let row"><app-status-chip [status]="row.status" /></td>
      </ng-container>

      <ng-container matColumnDef="priority">
        <th mat-header-cell *matHeaderCellDef>Priority</th>
        <td mat-cell *matCellDef="let row"><app-priority-chip [priority]="row.priority" /></td>
      </ng-container>

      <ng-container matColumnDef="assignee">
        <th mat-header-cell *matHeaderCellDef>Assigned to</th>
        <td mat-cell *matCellDef="let row">
          {{ row.assignedStaffName ?? '—' }}
        </td>
      </ng-container>

      <ng-container matColumnDef="sla">
        <th mat-header-cell *matHeaderCellDef>Response due</th>
        <td mat-cell *matCellDef="let row">
          <app-sla-badge
            [slaDueAt]="row.slaDueAt"
            [slaWarnedAt]="row.slaWarnedAt"
            [slaBreachedAt]="row.slaBreachedAt"
            [resolvedAt]="row.resolvedAt"
            [escalationLevel]="row.escalationLevel"
          />
        </td>
      </ng-container>

      <ng-container matColumnDef="created">
        <th mat-header-cell *matHeaderCellDef>Reported</th>
        <td mat-cell *matCellDef="let row">{{ row.createdAt | date: 'mediumDate' }}</td>
      </ng-container>

      <tr mat-header-row *matHeaderRowDef="columns()"></tr>
      <tr mat-row *matRowDef="let row; columns: columns()"></tr>
    </table>

    <mat-paginator
      [length]="page().total"
      [pageSize]="page().pageSize"
      [pageIndex]="page().page - 1"
      [pageSizeOptions]="[10, 20, 50]"
      (page)="paged.emit($event)"
    />
  `,
  styles: `
    .full {
      width: 100%;
    }
    .ref {
      font-family: monospace;
      font-weight: 600;
      text-decoration: none;
    }
    .title {
      font-weight: 500;
    }
    .meta {
      font-size: 12px;
      opacity: 0.65;
    }
  `,
})
export class ComplaintTable {
  readonly page = input.required<Page<ComplaintListItem>>();

  readonly columns = input<string[]>([
    'reference',
    'title',
    'status',
    'priority',
    'assignee',
    'sla',
    'created',
  ]);

  readonly paged = output<PageEvent>();
}
