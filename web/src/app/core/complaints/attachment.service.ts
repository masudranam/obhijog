import { HttpClient, HttpEvent, HttpEventType } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, map } from 'rxjs';

import { environment } from '../../../environments/environment';
import { Attachment } from '../models/api';

/** What the upload stream emits: progress until the server answers, then the row. */
export type UploadProgress =
  | { kind: 'progress'; percent: number }
  | { kind: 'done'; attachment: Attachment };

/**
 * Attachment upload and listing. SPEC.md §13.2, F6.
 *
 * The limits — type, size, count — are server policy and are not re-implemented here. The
 * form may hint at them for a faster message, but the server's `415`, `413` and `400` are
 * the decisions, and the interceptor surfaces them.
 */
@Injectable({ providedIn: 'root' })
export class AttachmentService {
  private readonly http = inject(HttpClient);

  list(complaintId: string): Observable<Attachment[]> {
    return this.http.get<Attachment[]>(this.urlFor(complaintId));
  }

  /**
   * Upload one file, reporting progress.
   *
   * `reportProgress` plus `observe: 'events'` is the only way to drive a determinate bar —
   * a plain post resolves once, at the end, which on a 5 MB photo over a phone connection
   * looks like the app has frozen.
   */
  upload(complaintId: string, file: File): Observable<UploadProgress> {
    const body = new FormData();
    body.append('file', file, file.name);

    return this.http
      .post<Attachment>(this.urlFor(complaintId), body, {
        reportProgress: true,
        observe: 'events',
      })
      .pipe(map((event) => this.toProgress(event)));
  }

  private toProgress(event: HttpEvent<Attachment>): UploadProgress {
    if (event.type === HttpEventType.UploadProgress) {
      // `total` is absent when the browser cannot determine the length; reporting 0 keeps
      // the bar honest rather than jumping to a made-up number.
      const percent = event.total ? Math.round((100 * event.loaded) / event.total) : 0;
      return { kind: 'progress', percent };
    }

    if (event.type === HttpEventType.Response && event.body) {
      return { kind: 'done', attachment: event.body };
    }

    return { kind: 'progress', percent: 0 };
  }

  private urlFor(complaintId: string): string {
    return `${environment.apiBaseUrl}/complaints/${complaintId}/attachments`;
  }
}
