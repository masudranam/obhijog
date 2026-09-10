import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressSpinnerModule } from '@angular/material/progress-spinner';
import { MatSelectModule } from '@angular/material/select';
import { Router } from '@angular/router';
import { toSignal } from '@angular/core/rxjs-interop';

import { ComplaintService } from '../../../core/complaints/complaint.service';
import { LatLng, MapPicker } from '../../../shared/map-picker/map-picker';

/**
 * F4 — a citizen files a complaint.
 *
 * The form mirrors the server's constraints so the user is told early, but the server is
 * the authority: it re-validates everything, resolves the department and priority from the
 * category, and allocates the reference number (§8.4, §8.10). Nothing here computes a
 * deadline or picks a department.
 */
@Component({
  selector: 'app-submit-complaint',
  imports: [
    ReactiveFormsModule,
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatIconModule,
    MatInputModule,
    MatProgressSpinnerModule,
    MatSelectModule,
    MapPicker,
  ],
  templateUrl: './submit.html',
  styleUrl: './submit.css',
})
export class SubmitComplaint {
  private readonly complaints = inject(ComplaintService);
  private readonly router = inject(Router);
  private readonly formBuilder = inject(FormBuilder);

  protected readonly categories = toSignal(this.complaints.categories(), { initialValue: [] });

  protected readonly submitting = signal(false);
  protected readonly failure = signal<string | null>(null);

  protected readonly form = this.formBuilder.nonNullable.group({
    title: ['', [Validators.required, Validators.maxLength(140)]],
    description: ['', [Validators.required, Validators.maxLength(4000)]],
    categoryId: ['', Validators.required],
    addressText: ['', Validators.maxLength(250)],
  });

  /**
   * Held outside the form: the map emits a pair, and Angular's validators have nothing
   * useful to say about a coordinate the user picked by dragging a pin.
   */
  protected readonly point = signal<LatLng | null>(null);

  protected onPicked(point: LatLng): void {
    this.point.set(point);
  }

  protected submit(): void {
    if (this.form.invalid || this.submitting()) {
      this.form.markAllAsTouched();
      return;
    }

    const point = this.point();
    if (!point) {
      this.failure.set('Place the pin on the map so the crew knows where to go.');
      return;
    }

    this.submitting.set(true);
    this.failure.set(null);

    const value = this.form.getRawValue();

    this.complaints
      .create({
        title: value.title,
        description: value.description,
        categoryId: value.categoryId,
        latitude: point.lat,
        longitude: point.lng,
        addressText: value.addressText || null,
      })
      .subscribe({
        next: (created) => {
          // The reference number is on the detail screen, which is also where a citizen
          // will come back to. Passing it in state avoids a second fetch for the toast.
          this.router.navigate(['/my', created.id], {
            state: { justSubmitted: true },
          });
        },
        error: () => {
          // The error interceptor has already shown the ProblemDetails in a snackbar; this
          // only re-enables the button.
          this.submitting.set(false);
        },
      });
  }
}
