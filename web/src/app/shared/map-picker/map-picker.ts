import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatIconModule } from '@angular/material/icon';
import * as L from 'leaflet';

export interface LatLng {
  lat: number;
  lng: number;
}

/**
 * Leaflet over OpenStreetMap tiles, confined to this one component. SPEC.md §15.
 *
 * No API key, no billing account and no second mapping library. Leaflet's CSS is imported
 * once in `styles.css` rather than per component.
 *
 * Two modes: draggable pin plus "use my location" for submission, and a read-only pin for
 * the detail screen.
 */
@Component({
  selector: 'app-map-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MatButtonModule, MatIconModule],
  template: `
    <div class="wrap">
      <div #map class="map" [class.readonly]="readonly()"></div>

      @if (!readonly()) {
        <div class="controls">
          <button mat-stroked-button type="button" (click)="useMyLocation()">
            <mat-icon>my_location</mat-icon>
            Use my location
          </button>
          <span class="coords">{{ lat().toFixed(5) }}, {{ lng().toFixed(5) }}</span>
        </div>
        @if (locationError()) {
          <p class="error">{{ locationError() }}</p>
        }
      }
    </div>
  `,
  styles: `
    .wrap {
      display: block;
    }
    .map {
      height: 300px;
      width: 100%;
      border-radius: 8px;
      border: 1px solid rgba(0, 0, 0, 0.12);
    }
    .map.readonly {
      height: 220px;
    }
    .controls {
      display: flex;
      align-items: center;
      gap: 12px;
      margin-top: 8px;
      flex-wrap: wrap;
    }
    .coords {
      font-family: monospace;
      font-size: 12px;
      opacity: 0.75;
    }
    .error {
      color: #b71c1c;
      font-size: 12px;
      margin: 4px 0 0;
    }
  `,
})
export class MapPicker implements AfterViewInit, OnDestroy {
  /** Dhaka. A starting view only — never submitted unless the user leaves the pin there. */
  private static readonly DefaultCentre: LatLng = { lat: 23.8103, lng: 90.4125 };

  readonly initialLat = input<number>(MapPicker.DefaultCentre.lat);
  readonly initialLng = input<number>(MapPicker.DefaultCentre.lng);
  readonly readonly = input<boolean>(false);

  readonly picked = output<LatLng>();

  protected readonly lat = signal(MapPicker.DefaultCentre.lat);
  protected readonly lng = signal(MapPicker.DefaultCentre.lng);
  protected readonly locationError = signal<string | null>(null);

  private readonly host = viewChild.required<ElementRef<HTMLDivElement>>('map');

  private map?: L.Map;
  private marker?: L.Marker;

  ngAfterViewInit(): void {
    this.lat.set(this.initialLat());
    this.lng.set(this.initialLng());

    this.map = L.map(this.host().nativeElement, {
      center: [this.lat(), this.lng()],
      zoom: 14,
      // A read-only map is a picture: dragging it invites the user to think it is editable.
      dragging: !this.readonly(),
      scrollWheelZoom: !this.readonly(),
      zoomControl: !this.readonly(),
    });

    L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
      maxZoom: 19,
      attribution: '&copy; OpenStreetMap contributors',
    }).addTo(this.map);

    this.marker = L.marker([this.lat(), this.lng()], {
      draggable: !this.readonly(),
      icon: MapPicker.icon(),
    }).addTo(this.map);

    if (!this.readonly()) {
      this.marker.on('dragend', () => this.commit(this.marker!.getLatLng()));
      this.map.on('click', (event: L.LeafletMouseEvent) => {
        this.marker!.setLatLng(event.latlng);
        this.commit(event.latlng);
      });
    }
  }

  ngOnDestroy(): void {
    this.map?.remove();
  }

  protected useMyLocation(): void {
    if (!navigator.geolocation) {
      this.locationError.set('This browser cannot report a location. Drag the pin instead.');
      return;
    }

    this.locationError.set(null);

    navigator.geolocation.getCurrentPosition(
      (position) => {
        const point = L.latLng(position.coords.latitude, position.coords.longitude);
        this.marker?.setLatLng(point);
        this.map?.setView(point, 16);
        this.commit(point);
      },
      () =>
        this.locationError.set(
          'Could not read your location. Check the browser permission, or drag the pin instead.',
        ),
    );
  }

  private commit(point: L.LatLng): void {
    this.lat.set(point.lat);
    this.lng.set(point.lng);
    this.picked.emit({ lat: point.lat, lng: point.lng });
  }

  /**
   * Leaflet's default icon resolves its images by a relative URL that a bundled build does
   * not serve. A small inline SVG avoids shipping the sprite and the retina variant, and
   * avoids the well-known broken-marker bug entirely.
   */
  private static icon(): L.DivIcon {
    return L.divIcon({
      className: 'obhijog-pin',
      html:
        '<svg xmlns="http://www.w3.org/2000/svg" width="28" height="40" viewBox="0 0 24 34">'
        + '<path fill="#b3261e" stroke="#fff" stroke-width="1.5" d="M12 1C6.9 1 2.8 5.1 2.8 10.2'
        + 'c0 6.9 8.1 21.3 8.4 21.9a.9.9 0 0 0 1.6 0c.3-.6 8.4-15 8.4-21.9C21.2 5.1 17.1 1 12 1z"/>'
        + '<circle cx="12" cy="10.2" r="3.6" fill="#fff"/></svg>',
      iconSize: [28, 40],
      iconAnchor: [14, 40],
    });
  }
}
