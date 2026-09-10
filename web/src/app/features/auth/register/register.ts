import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { Router, RouterLink } from '@angular/router';

import { AuthService } from '../../../core/auth/auth.service';

/**
 * Citizen self-registration only (SPEC.md §10.1). There is no role or department field,
 * and the API's request type has none either — Staff and DeptAdmin accounts come from
 * seed or from `POST /users` by a DeptAdmin.
 */
@Component({
  selector: 'app-register',
  imports: [
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    ReactiveFormsModule,
    RouterLink,
  ],
  templateUrl: './register.html',
  styleUrl: './register.css',
})
export class Register {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  protected readonly busy = signal(false);

  /** Ten characters, matching the server's `Password.RequiredLength` (§17). */
  protected readonly form = inject(FormBuilder).nonNullable.group({
    fullName: ['', [Validators.required, Validators.maxLength(120)]],
    email: ['', [Validators.required, Validators.email]],
    phone: ['', [Validators.maxLength(24)]],
    password: ['', [Validators.required, Validators.minLength(10)]],
  });

  protected submit(): void {
    if (this.form.invalid || this.busy()) {
      return;
    }

    this.busy.set(true);
    const { fullName, email, phone, password } = this.form.getRawValue();

    this.auth.register({ fullName, email, password, phone: phone || null }).subscribe({
      next: () =>
        this.auth.loadMe().subscribe({
          next: (me) => void this.router.navigateByUrl(this.auth.landingRoute(me.role)),
          error: () => this.busy.set(false),
        }),
      error: () => this.busy.set(false),
    });
  }
}
