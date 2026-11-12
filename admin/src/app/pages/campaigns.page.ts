import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { Observable, firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { CAMPAIGN_STATUSES, Campaign, CampaignStatus } from '../core/models';
import { NotifyService } from '../shared/notify.service';

@Component({
  selector: 'adv-campaigns',
  imports: [FormsModule, RouterLink, MatTableModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatProgressBarModule],
  template: `
    <div class="page-head">
      <h1>Campaigns</h1>
      @if (auth.isAdmin()) { <a mat-flat-button routerLink="/campaigns/new">New campaign</a> }
    </div>
    <div class="filters">
      <mat-form-field appearance="outline">
        <mat-label>Search</mat-label>
        <input matInput [ngModel]="search()" (ngModelChange)="search.set($event)" placeholder="name, id, advertiser" />
      </mat-form-field>
      <mat-form-field appearance="outline">
        <mat-label>Status</mat-label>
        <mat-select [ngModel]="status()" (ngModelChange)="status.set($event)">
          <mat-option value="">All</mat-option>
          @for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    <table mat-table [dataSource]="filtered()" class="full">
      <ng-container matColumnDef="name">
        <th mat-header-cell *matHeaderCellDef>Name</th>
        <td mat-cell *matCellDef="let c"><a [routerLink]="['/campaigns', c.id]">{{ c.name }}</a><div class="muted small">{{ c.id }}</div></td>
      </ng-container>
      <ng-container matColumnDef="advertiser">
        <th mat-header-cell *matHeaderCellDef>Advertiser</th>
        <td mat-cell *matCellDef="let c">{{ c.advertiserId }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let c"><span [class]="'status ' + c.status">{{ c.status }}</span></td>
      </ng-container>
      <ng-container matColumnDef="bids">
        <th mat-header-cell *matHeaderCellDef>Bids</th>
        <td mat-cell *matCellDef="let c">{{ bids(c) }}</td>
      </ng-container>
      <ng-container matColumnDef="targeting">
        <th mat-header-cell *matHeaderCellDef>Targeting</th>
        <td mat-cell *matCellDef="let c" class="small">{{ targeting(c) }}</td>
      </ng-container>
      <ng-container matColumnDef="priority">
        <th mat-header-cell *matHeaderCellDef class="num">Prio</th>
        <td mat-cell *matCellDef="let c" class="num">{{ c.priority }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let c" class="actions">
          @if (auth.isAdmin()) {
            @if (c.status === 'active') {
              <button mat-button (click)="run(api.pauseCampaign(c.id), 'Paused ' + c.name)">Pause</button>
            } @else if (c.status !== 'ended') {
              <button mat-button (click)="run(api.resumeCampaign(c.id), 'Activated ' + c.name)">Activate</button>
            }
            <button mat-button class="danger" (click)="remove(c)">Delete</button>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="cols"></tr>
      <tr mat-row *matRowDef="let row; columns: cols"></tr>
    </table>
    @if (!loading() && filtered().length === 0) { <p class="muted">No campaigns match.</p> }
  `,
})
export class CampaignsPage {
  protected readonly api = inject(ApiService);
  protected readonly auth = inject(AuthService);
  private readonly notify = inject(NotifyService);

  readonly statuses = CAMPAIGN_STATUSES;
  readonly cols = ['name', 'advertiser', 'status', 'bids', 'targeting', 'priority', 'actions'];
  readonly campaigns = signal<Campaign[]>([]);
  readonly loading = signal(false);
  readonly search = signal('');
  readonly status = signal<CampaignStatus | ''>('');
  readonly filtered = computed(() => {
    const q = this.search().trim().toLowerCase();
    const st = this.status();
    return this.campaigns()
      .filter((c) => !st || c.status === st)
      .filter((c) => !q || [c.name, c.id, c.advertiserId].some((v) => v.toLowerCase().includes(q)))
      .sort((a, b) => a.name.localeCompare(b.name));
  });

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loading.set(true);
    try {
      this.campaigns.set(await firstValueFrom(this.api.campaigns()));
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.loading.set(false);
    }
  }

  bids(c: Campaign): string {
    return (c.bids ?? []).map((b) => `${b.model.toUpperCase()} ${b.amount}`).join(', ') || '–';
  }

  targeting(c: Campaign): string {
    const t = c.targeting;
    const parts = [...(t?.brands ?? []), ...(t?.keywords ?? []), ...(t?.categories ?? []), ...(t?.entities ?? []).map((e) => e.label)];
    return parts.length > 6 ? parts.slice(0, 6).join(', ') + ' …' : parts.join(', ') || '–';
  }

  async run(op: Observable<unknown>, message: string): Promise<void> {
    try {
      await firstValueFrom(op);
      this.notify.ok(message);
      await this.load();
    } catch (e) {
      this.notify.error(e);
    }
  }

  async remove(c: Campaign): Promise<void> {
    if (!confirm(`Delete campaign "${c.name}"? This cannot be undone.`)) return;
    await this.run(this.api.deleteCampaign(c.id), `Deleted ${c.name}`);
  }
}
