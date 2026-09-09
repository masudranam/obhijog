import { Component } from '@angular/core';
import { MatCardModule } from '@angular/material/card';

import { environment } from '../../../environments/environment';

@Component({
  selector: 'app-home',
  imports: [MatCardModule],
  templateUrl: './home.html',
  styleUrl: './home.css',
})
export class Home {
  protected readonly apiBaseUrl = environment.apiBaseUrl;
}
