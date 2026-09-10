import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatIconModule } from '@angular/material/icon';
import { RouterLink } from '@angular/router';

import { AuthService } from '../../core/auth/auth.service';

/**
 * The public landing page.
 *
 * Its job is to route three kinds of visitor: someone with a problem to report, someone
 * holding a reference number who does not want an account, and a member of staff signing
 * in. Tracking is deliberately offered before signing in — §9.5 exists so that a citizen
 * never has to register to find out what happened to their complaint.
 */
@Component({
  selector: 'app-home',
  imports: [MatButtonModule, MatCardModule, MatIconModule, RouterLink],
  templateUrl: './home.html',
  styleUrl: './home.css',
})
export class Home {
  protected readonly auth = inject(AuthService);
}
