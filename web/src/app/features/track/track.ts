import { DatePipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';

import { ComplaintService } from '../../core/complaints/complaint.service';
import { PublicComplaint } from '../../core/models/api';
import { StatusChip } from '../../shared/status-chip/status-chip';

/**
 * F5 — public tracking by reference number, with no account.
 *
 * What this screen can show is decided entirely by §9.5: the server returns a redacted
 * projection and there is nothing else to render. No citizen or staff name, no address, no
 * coordinates, no comments. A reference number is guessable, so that projection *is* the
 * security boundary — and the smaller this template is, the more obviously it holds.
 */
@Component({
  selector: 'app-track',
  imports: [
    DatePipe,
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    StatusChip,
  ],
  templateUrl: './track.html',
  styleUrl: './track.css',
})
export class Track {
  private readonly complaints = inject(ComplaintService);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly form = this.formBuilder.nonNullable.group({
    reference: ['', [Validators.required, Validators.pattern(/^\s*MC-\d{4}-\d{6}\s*$/i)]],
  });

  protected readonly result = signal<PublicComplaint | null>(null);
  protected readonly notFound = signal(false);
  protected readonly searching = signal(false);

  protected track(): void {
    if (this.form.invalid) {
      this.form.markAllAsTouched();
      return;
    }

    this.searching.set(true);
    this.notFound.set(false);
    this.result.set(null);

    this.complaints.getByReference(this.form.getRawValue().reference.trim()).subscribe({
      next: (complaint) => {
        this.result.set(complaint);
        this.searching.set(false);
      },
      error: () => {
        // A 404 here is an ordinary outcome, not an error worth a snackbar: most of the
        // time the citizen has simply mistyped a digit.
        this.notFound.set(true);
        this.searching.set(false);
      },
    });
  }
}
