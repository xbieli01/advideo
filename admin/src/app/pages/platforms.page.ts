import { Component, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { PlatformConfig } from '../core/models';
import { NotifyService } from '../shared/notify.service';

@Component({
  selector: 'adv-platforms',
  imports: [RouterLink, MatTableModule, MatButtonModule, MatProgressBarModule],
  template: `
    <div class="page-head">
      <h1>Platforms</h1>
      @if (auth.isAdmin()) { <a mat-flat-button routerLink="/platforms/new">New platform</a> }
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <table mat-table [dataSource]="platforms()" class="full">
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Name</th>
        <td mat-cell *matCellDef="let p"><a [routerLink]="['/platforms', p.id]">{{ p.name }}</a><div class="muted small">{{ p.id }}</div></td>
      </ng-container>
      <ng-container matColumnDef="kind">
        <th mat-header-cell *matHeaderCellDef>Kind</th>
        <td mat-cell *matCellDef="let p">{{ p.kind }}</td>
      </ng-container>
      <ng-container matColumnDef="pricing">
        <th mat-header-cell *matHeaderCellDef>Pricing</th>
        <td mat-cell *matCellDef="let p">{{ p.pricing?.enabledModels?.join(', ')?.toUpperCase() }} · {{ p.pricing?.currency }}</td>
      </ng-container>
      <ng-container matColumnDef="slots">
        <th mat-header-cell *matHeaderCellDef>Slots</th>
        <td mat-cell *matCellDef="let p" class="small">{{ slots(p) }}</td>
      </ng-container>
      <ng-container matColumnDef="frequency">
        <th mat-header-cell *matHeaderCellDef>Frequency</th>
        <td mat-cell *matCellDef="let p" class="small">every ≥ {{ p.frequency?.minIntervalSec }} s, max {{ p.frequency?.maxPerVideo }}/video</td>
      </ng-container>
      <ng-container matColumnDef="threshold">
        <th mat-header-cell *matHeaderCellDef class="num">Relevance ≥</th>
        <td mat-cell *matCellDef="let p" class="num">{{ p.matching?.relevanceThreshold }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let p" class="actions">
          @if (auth.isAdmin()) { <button mat-button class="danger" (click)="remove(p)">Delete</button> }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="cols"></tr>
      <tr mat-row *matRowDef="let row; columns: cols"></tr>
    </table>
  `,
})
export class PlatformsPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly auth = inject(AuthService);

  readonly cols = ['name', 'kind', 'pricing', 'slots', 'frequency', 'threshold', 'actions'];
  readonly platforms = signal<PlatformConfig[]>([]);
  readonly loading = signal(false);

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.platforms.set(await firstValueFrom(this.api.platforms()));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.loading.set(false);
    }
  }

  slots(p: PlatformConfig): string {
    return (p.slots ?? []).map((s) => `${s.id} (${s.position} ${s.width}×${s.height})`).join(', ') || '–';
  }

  async remove(p: PlatformConfig): Promise<void> {
    if (!confirm(`Delete platform "${p.name}"? Its snippet integration stops serving ads.`)) return;
    try {
      await firstValueFrom(this.api.deletePlatform(p.id));
      this.notify.ok(`Deleted ${p.name}`);
      await this.load();
    } catch (e) {
      this.notify.error(e);
    }
  }
}
