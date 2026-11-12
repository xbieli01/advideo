import { CurrencyPipe, DecimalPipe, PercentPipe } from '@angular/common';
import { Component, computed, inject, signal } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { catchError, firstValueFrom, forkJoin, of } from 'rxjs';
import { ApiService } from '../core/api.service';
import { Campaign, CampaignStats } from '../core/models';
import { NotifyService } from '../shared/notify.service';

interface Row {
  campaign: Campaign;
  stats: CampaignStats | null;
}

@Component({
  selector: 'adv-overview',
  imports: [MatCardModule, MatTableModule, MatProgressBarModule, MatButtonModule, RouterLink, DecimalPipe, PercentPipe, CurrencyPipe],
  template: `
    <div class="page-head">
      <h1>Overview</h1>
      <button mat-stroked-button (click)="load()">Refresh</button>
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <div class="kpis">
      <mat-card><mat-card-content><div class="kpi-label">Active campaigns</div><div class="kpi">{{ activeCount() }} / {{ rows().length }}</div></mat-card-content></mat-card>
      <mat-card><mat-card-content><div class="kpi-label">Impressions</div><div class="kpi">{{ totals().impressions | number }}</div></mat-card-content></mat-card>
      <mat-card><mat-card-content><div class="kpi-label">Clicks · CTR</div><div class="kpi">{{ totals().clicks | number }} · {{ totals().ctr | percent: '1.2-2' }}</div></mat-card-content></mat-card>
      <mat-card><mat-card-content><div class="kpi-label">Spent today / total</div><div class="kpi">{{ totals().spentToday | currency: 'EUR' }} / {{ totals().spentTotal | currency: 'EUR' }}</div></mat-card-content></mat-card>
    </div>
    <table mat-table [dataSource]="rows()" class="full">
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Campaign</th>
        <td mat-cell *matCellDef="let r"><a [routerLink]="['/campaigns', r.campaign.id]">{{ r.campaign.name }}</a><div class="muted small">{{ r.campaign.advertiserId }}</div></td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let r"><span class="status" [class]="'status ' + r.campaign.status">{{ r.campaign.status }}</span></td>
      </ng-container>
      <ng-container matColumnDef="impressions">
        <th mat-header-cell *matHeaderCellDef class="num">Impr.</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.stats?.impressions ?? 0 | number }}</td>
      </ng-container>
      <ng-container matColumnDef="viewables">
        <th mat-header-cell *matHeaderCellDef class="num">Viewable</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.stats?.viewables ?? 0 | number }}</td>
      </ng-container>
      <ng-container matColumnDef="clicks">
        <th mat-header-cell *matHeaderCellDef class="num">Clicks</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.stats?.clicks ?? 0 | number }}</td>
      </ng-container>
      <ng-container matColumnDef="ctr">
        <th mat-header-cell *matHeaderCellDef class="num">CTR</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.stats?.ctr ?? 0 | percent: '1.2-2' }}</td>
      </ng-container>
      <ng-container matColumnDef="spent">
        <th mat-header-cell *matHeaderCellDef class="num">Spent today / total</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.stats?.spentToday ?? 0 | currency: 'EUR' }} / {{ r.stats?.spentTotal ?? 0 | currency: 'EUR' }}</td>
      </ng-container>
      <ng-container matColumnDef="budget">
        <th mat-header-cell *matHeaderCellDef class="num">Budget daily / total</th>
        <td mat-cell *matCellDef="let r" class="num">{{ r.campaign.budget?.daily ?? '–' }} / {{ r.campaign.budget?.total ?? '–' }}</td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="cols"></tr>
      <tr mat-row *matRowDef="let row; columns: cols"></tr>
    </table>
    @if (!loading() && rows().length === 0) { <p class="muted">No campaigns yet.</p> }
  `,
})
export class OverviewPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  readonly cols = ['name', 'status', 'impressions', 'viewables', 'clicks', 'ctr', 'spent', 'budget'];
  readonly rows = signal<Row[]>([]);
  readonly loading = signal(false);
  readonly activeCount = computed(() => this.rows().filter((r) => r.campaign.status === 'active').length);
  readonly totals = computed(() => {
    const s = this.rows().map((r) => r.stats).filter((x): x is CampaignStats => !!x);
    const impressions = s.reduce((a, x) => a + x.impressions, 0);
    const clicks = s.reduce((a, x) => a + x.clicks, 0);
    return {
      impressions,
      clicks,
      ctr: impressions ? clicks / impressions : 0,
      spentToday: s.reduce((a, x) => a + x.spentToday, 0),
      spentTotal: s.reduce((a, x) => a + x.spentTotal, 0),
    };
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      const campaigns = await firstValueFrom(this.api.campaigns());
      const stats = campaigns.length
        ? await firstValueFrom(forkJoin(campaigns.map((c) => this.api.campaignStats(c.id).pipe(catchError(() => of(null))))))
        : [];
      this.rows.set(campaigns.map((c, i) => ({ campaign: c, stats: stats[i] })).sort((a, b) => a.campaign.name.localeCompare(b.campaign.name)));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.loading.set(false);
    }
  }
}
