import { DecimalPipe } from '@angular/common';
import { Component, computed, inject, input, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import {
  MAT_DIALOG_DATA,
  MatDialog,
  MatDialogModule,
  MatDialogRef,
} from '@angular/material/dialog';
import { MatIconModule } from '@angular/material/icon';
import { MatProgressBarModule } from '@angular/material/progress-bar';

import { AttachmentService } from '../../core/complaints/attachment.service';
import { Attachment } from '../../core/models/api';

/**
 * Photo upload and thumbnail gallery. SPEC.md §15, F6.
 *
 * Thumbnails render straight from each row's `readUrl`, a short-lived SAS. The component
 * knows nothing about the size, type or count limits — those are server policy, and the
 * `415` / `413` / `400` come back through the error interceptor as a snackbar.
 */
@Component({
  selector: 'app-attachment-gallery',
  imports: [
    DecimalPipe,
    MatButtonModule,
    MatDialogModule,
    MatIconModule,
    MatProgressBarModule,
  ],
  templateUrl: './attachment-gallery.html',
  styleUrl: './attachment-gallery.css',
})
export class AttachmentGallery {
  private readonly attachments = inject(AttachmentService);
  private readonly dialog = inject(MatDialog);

  readonly complaintId = input.required<string>();
  readonly initial = input<Attachment[]>([]);

  /** Whether to offer the upload control at all. The server still decides (§15). */
  readonly canUpload = input<boolean>(true);

  private readonly added = signal<Attachment[]>([]);

  protected readonly items = computed<Attachment[]>(() => [...this.initial(), ...this.added()]);

  protected readonly uploading = signal(false);
  protected readonly percent = signal(0);

  protected onFile(event: Event): void {
    const input = event.target as HTMLInputElement;
    const file = input.files?.[0];

    if (!file) {
      return;
    }

    this.uploading.set(true);
    this.percent.set(0);

    this.attachments.upload(this.complaintId(), file).subscribe({
      next: (progress) => {
        if (progress.kind === 'progress') {
          this.percent.set(progress.percent);
          return;
        }

        this.added.update((current) => [...current, progress.attachment]);
        this.uploading.set(false);
      },
      error: () => {
        // The interceptor has already shown the server's ProblemDetails.
        this.uploading.set(false);
      },
      complete: () => this.uploading.set(false),
    });

    // Clear it, so choosing the same file twice in a row fires a change event both times.
    input.value = '';
  }

  protected open(attachment: Attachment): void {
    this.dialog.open(AttachmentLightbox, { data: attachment, maxWidth: '96vw' });
  }
}

/** A full-size view of one photo. Nothing more, so it lives beside its only caller. */
@Component({
  selector: 'app-attachment-lightbox',
  imports: [MatButtonModule, MatDialogModule, MatIconModule],
  template: `
    <div class="lightbox">
      <img [src]="data.readUrl" [alt]="data.originalFileName" />
      <div class="bar">
        <span class="name">{{ data.originalFileName }}</span>
        <button mat-icon-button (click)="close()" aria-label="Close">
          <mat-icon>close</mat-icon>
        </button>
      </div>
    </div>
  `,
  styles: `
    .lightbox {
      display: flex;
      flex-direction: column;
    }
    img {
      max-width: 100%;
      max-height: 78vh;
      object-fit: contain;
      background: #000;
    }
    .bar {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
      padding: 8px 4px 0;
    }
    .name {
      font-size: 13px;
      overflow: hidden;
      text-overflow: ellipsis;
      white-space: nowrap;
    }
  `,
})
export class AttachmentLightbox {
  protected readonly data = inject<Attachment>(MAT_DIALOG_DATA);
  private readonly ref = inject<MatDialogRef<AttachmentLightbox>>(MatDialogRef);

  protected close(): void {
    this.ref.close();
  }
}
