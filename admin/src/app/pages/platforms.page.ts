import { Component, inject, signal } from '@angular/core';
import { I18nService, TranslatePipe } from '../core/i18n.service';
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
  imports: [TranslatePipe, RouterLink, MatTableModule, MatButtonModule, MatProgressBarModule],
  template: `
    <div class="page-head">
      <h1>{{ 'Platforms' | t }}</h1>
      @if (auth.isAdmin()) { <a mat-flat-button routerLink="/platforms/new">{{ 'New platform' | t }}</a> }
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <table mat-table [dataSource]="platforms()" class="full">
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>{{ 'Name' | t }}</th>
        <td mat-cell *matCellDef="let p"><a [routerLink]="['/platforms', p.id]">{{ p.name }}</a><div class="muted small">{{ p.id }}</div></td>
      </ng-container>
      <ng-container matColumnDef="kind">
        <th mat-header-cell *matHeaderCellDef>{{ 'Kind' | t }}</th>
        <td mat-cell *matCellDef="let p">{{ p.kind }}</td>
      </ng-container>
      <ng-container matColumnDef="pricing">
        <th mat-header-cell *matHeaderCellDef>{{ 'Pricing' | t }}</th>
        <td mat-cell *matCellDef="let p">{{ p.pricing?.enabledModels?.join(', ')?.toUpperCase() }} · {{ p.pricing?.currency }}</td>
      </ng-container>
      <ng-container matColumnDef="slots">
        <th mat-header-cell *matHeaderCellDef>{{ 'Slots' | t }}</th>
        <td mat-cell *matCellDef="let p" class="small">{{ slots(p) }}</td>
      </ng-container>
      <ng-container matColumnDef="frequency">
        <th mat-header-cell *matHeaderCellDef>{{ 'Frequency' | t }}</th>
        <td mat-cell *matCellDef="let p" class="small">{{ 'every ≥ {sec} s, max {max}/video' | t: { sec: p.frequency?.minIntervalSec, max: p.frequency?.maxPerVideo } }}</td>
      </ng-container>
      <ng-container matColumnDef="threshold">
        <th mat-header-cell *matHeaderCellDef class="num">{{ 'Relevance ≥' | t }}</th>
        <td mat-cell *matCellDef="let p" class="num">{{ p.matching?.relevanceThreshold }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let p" class="actions">
          @if (auth.isAdmin()) { <button mat-button class="danger" (click)="remove(p)">{{ 'Delete' | t }}</button> }
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
  protected readonly i18n = inject(I18nService);
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
    if (!confirm(this.i18n.t('Delete platform "{name}"? Its snippet integration stops serving ads.', { name: p.name }))) return;
    try {
      await firstValueFrom(this.api.deletePlatform(p.id));
      this.notify.ok(this.i18n.t('Deleted {name}', { name: p.name }));
      await this.load();
    } catch (e) {
      this.notify.error(e);
    }
  }
}
