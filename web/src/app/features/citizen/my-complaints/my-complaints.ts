import { Component, inject } from '@angular/core';
import { MatCardModule } from '@angular/material/card';

import { AuthService } from '../../../core/auth/auth.service';

/**
 * Role landing route (SPEC.md §14 F3). A placeholder until the milestone that fills it:
 * the real screens arrive with the features they belong to.
 */
@Component({
  selector: 'app-my-complaints',
  imports: [MatCardModule],
  templateUrl: './my-complaints.html',
})
export class MyComplaints {
  protected readonly me = inject(AuthService).me;
}
