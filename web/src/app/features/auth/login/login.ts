import { Component, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';

import { AuthService } from '../../../core/auth/auth.service';

@Component({
  selector: 'app-login',
  imports: [
    MatButtonModule,
    MatCardModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    ReactiveFormsModule,
    RouterLink,
  ],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  protected readonly busy = signal(false);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    password: ['', [Validators.required]],
  });

  protected submit(): void {
    if (this.form.invalid || this.busy()) {
      return;
    }

    this.busy.set(true);

    this.auth.login(this.form.getRawValue()).subscribe({
      next: () => this.landAfterLogin(),
      // The error interceptor has already shown the ProblemDetails; this only re-enables
      // the form.
      error: () => this.busy.set(false),
    });
  }

  /**
   * Role decides the landing route (SPEC.md §14 F3), so `me` has to resolve before we can
   * navigate. A `returnUrl` from the guard wins when present.
   */
  private landAfterLogin(): void {
    this.auth.loadMe().subscribe({
      next: (me) => {
        const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl');
        void this.router.navigateByUrl(returnUrl ?? this.auth.landingRoute(me.role));
      },
      error: () => this.busy.set(false),
    });
  }
}
