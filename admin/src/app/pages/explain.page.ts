import { CurrencyPipe, DecimalPipe } from '@angular/common';
import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatTableModule } from '@angular/material/table';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { ExplainCandidate, ExplainResponse, PlatformConfig } from '../core/models';
import { NotifyService } from '../shared/notify.service';

@Component({
  selector: 'adv-explain',
  imports: [FormsModule, RouterLink, DecimalPipe, CurrencyPipe, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, MatTableModule],
  template: `
    <div class="page-head"><h1>Explain decision</h1></div>
    <p class="muted">Which campaign would win at a given moment of a video, and why the others did not. Nothing is delivered or charged.</p>
    <form #form="ngForm" class="row" (ngSubmit)="run()">
      <mat-form-field appearance="outline"><mat-label>Platform</mat-label>
        <mat-select name="platform" [(ngModel)]="platform" required>@for (p of platforms(); track p.id) { <mat-option [value]="p.id">{{ p.name }}</mat-option> }</mat-select></mat-form-field>
      <mat-form-field appearance="outline"><mat-label>Video id</mat-label><input matInput name="videoId" [(ngModel)]="videoId" required /></mat-form-field>
      <mat-form-field appearance="outline" class="narrow"><mat-label>Time (s)</mat-label><input matInput type="number" min="0" step="0.5" name="t" [(ngModel)]="t" required /></mat-form-field>
      <button mat-flat-button type="submit" [disabled]="busy() || form.invalid">Explain</button>
    </form>
    @if (result(); as r) {
      <mat-card class="section">
        <mat-card-content>
          @if (r.winnerCampaignId) {
            <p class="winner">Winner: <a [routerLink]="['/campaigns', r.winnerCampaignId]">{{ r.winnerCampaignId }}</a></p>
          } @else {
            <p class="error">No ad: {{ r.noAdReason || 'no matching campaign' }}</p>
          }
          <p class="muted small">Segment {{ r.segment ? (r.segment.startSec | number: '1.0-1') + '–' + (r.segment.endSec | number: '1.0-1') + ' s' : 'none at this time' }}</p>
        </mat-card-content>
      </mat-card>
      <table mat-table [dataSource]="r.candidates" class="full">
        <ng-container matColumnDef="campaign">
          <th mat-header-cell *matHeaderCellDef>Campaign</th>
          <td mat-cell *matCellDef="let c"><a [routerLink]="['/campaigns', c.campaignId]">{{ c.campaignId }}</a></td>
        </ng-container>
        <ng-container matColumnDef="relevance">
          <th mat-header-cell *matHeaderCellDef class="num">Relevance</th>
          <td mat-cell *matCellDef="let c" class="num">{{ c.relevance | number: '1.2-3' }}</td>
        </ng-container>
        <ng-container matColumnDef="ecpm">
          <th mat-header-cell *matHeaderCellDef class="num">eCPM</th>
          <td mat-cell *matCellDef="let c" class="num">{{ c.ecpm | currency: 'EUR' }}</td>
        </ng-container>
        <ng-container matColumnDef="rank">
          <th mat-header-cell *matHeaderCellDef class="num">Rank</th>
          <td mat-cell *matCellDef="let c" class="num">{{ c.rank | number: '1.2-3' }}</td>
        </ng-container>
        <ng-container matColumnDef="matched">
          <th mat-header-cell *matHeaderCellDef>Matched on</th>
          <td mat-cell *matCellDef="let c" class="small">{{ matched(c) }}</td>
        </ng-container>
        <ng-container matColumnDef="result">
          <th mat-header-cell *matHeaderCellDef>Result</th>
          <td mat-cell *matCellDef="let c" class="small">
            @if (c.rejectedBecause) { <span class="error">{{ c.rejectedBecause }}</span> }
            @else if (c.campaignId === r.winnerCampaignId) { <span class="status active">winner</span> · {{ c.creativeId }} @ {{ c.slotId }} }
            @else { eligible }
          </td>
        </ng-container>
        <tr mat-header-row *matHeaderRowDef="cols"></tr>
        <tr mat-row *matRowDef="let row; columns: cols"></tr>
      </table>
      @if (r.candidates.length === 0) { <p class="muted">No campaign matched this segment.</p> }
    }
  `,
})
export class ExplainPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  readonly cols = ['campaign', 'relevance', 'ecpm', 'rank', 'matched', 'result'];
  readonly platforms = signal<PlatformConfig[]>([]);
  readonly result = signal<ExplainResponse | null>(null);
  readonly busy = signal(false);
  platform = '';
  videoId = inject(ActivatedRoute).snapshot.queryParamMap.get('videoId') ?? '';
  t = 0;

  async ngOnInit(): Promise<void> {
    try {
      const ps = await firstValueFrom(this.api.platforms());
      this.platforms.set(ps);
      if (!this.platform && ps.length) this.platform = ps[0].id;
    } catch (e) {
      this.notify.error(e);
    }
  }

  matched(c: ExplainCandidate): string {
    return c.matchedOn.map((m) => `${m.kind}:${m.term} (${m.score.toFixed(2)})`).join(', ') || '–';
  }

  async run(): Promise<void> {
    this.busy.set(true);
    try {
      this.result.set(await firstValueFrom(this.api.explain(this.platform, this.videoId.trim(), Number(this.t))));
    } catch (e) {
      this.result.set(null);
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }
}
