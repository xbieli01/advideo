import { DatePipe, JsonPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { VideoJob } from '../core/models';
import { NotifyService } from '../shared/notify.service';

const ACTIVE = ['queued', 'running'];

@Component({
  selector: 'adv-videos',
  imports: [FormsModule, RouterLink, DatePipe, JsonPipe, MatCardModule, MatTableModule, MatButtonModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <div class="page-head">
      <h1>Videos</h1>
      <button mat-stroked-button (click)="load()">Refresh</button>
    </div>
    <mat-card class="section">
      <mat-card-header><mat-card-title>Analyze a video</mat-card-title><mat-card-subtitle>creates the content map used for ad matching</mat-card-subtitle></mat-card-header>
      <mat-card-content>
        <form #form="ngForm" class="row" (ngSubmit)="register()">
          <mat-form-field appearance="outline"><mat-label>Video id</mat-label><input matInput name="videoId" [(ngModel)]="videoId" required /></mat-form-field>
          <mat-form-field appearance="outline" class="wide"><mat-label>Video URI</mat-label><input matInput name="videoUri" [(ngModel)]="videoUri" required placeholder="/videos/demo.mp4 or s3://…" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>Analyzer</mat-label><input matInput name="analyzer" [(ngModel)]="analyzer" placeholder="yolo+clip" /><mat-hint>empty = default</mat-hint></mat-form-field>
          <button mat-flat-button type="submit" [disabled]="busy() || form.invalid">Queue analysis</button>
        </form>
      </mat-card-content>
    </mat-card>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (hasActive()) { <p class="muted small">Jobs in progress — refreshing every 5 s.</p> }
    <table mat-table [dataSource]="jobs()" class="full">
      <ng-container matColumnDef="video">
        <th mat-header-cell *matHeaderCellDef>Video</th>
        <td mat-cell *matCellDef="let j">{{ j.videoId }}<div class="muted small">{{ j.videoUri }}</div></td>
      </ng-container>
      <ng-container matColumnDef="analyzer">
        <th mat-header-cell *matHeaderCellDef>Analyzer</th>
        <td mat-cell *matCellDef="let j">{{ j.analyzer }}</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>Status</th>
        <td mat-cell *matCellDef="let j"><span [class]="'status ' + j.status.toLowerCase()">{{ j.status }}</span>
          @if (j.error) { <div class="error small">{{ j.error }}</div> }</td>
      </ng-container>
      <ng-container matColumnDef="updated">
        <th mat-header-cell *matHeaderCellDef>Updated</th>
        <td mat-cell *matCellDef="let j">{{ j.updatedAt | date: 'short' }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let j" class="actions">
          @if (j.mapKey) {
            <button mat-button (click)="showMap(j)">Content map</button>
            <a mat-button routerLink="/explain" [queryParams]="{ videoId: j.videoId }">Explain</a>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="cols"></tr>
      <tr mat-row *matRowDef="let row; columns: cols"></tr>
    </table>
    @if (!loading() && jobs().length === 0) { <p class="muted">No videos analyzed yet.</p> }
    @if (map(); as m) {
      <mat-card class="section">
        <mat-card-header><mat-card-title>Content map · {{ mapVideoId() }}</mat-card-title></mat-card-header>
        <mat-card-content>
          <pre class="json">{{ m | json }}</pre>
          <button mat-button (click)="map.set(null)">Close</button>
        </mat-card-content>
      </mat-card>
    }
  `,
})
export class VideosPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);

  readonly cols = ['video', 'analyzer', 'status', 'updated', 'actions'];
  readonly jobs = signal<VideoJob[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly map = signal<unknown>(null);
  readonly mapVideoId = signal('');
  readonly hasActive = computed(() => this.jobs().some((j) => ACTIVE.includes(j.status.toLowerCase())));
  videoId = '';
  videoUri = '';
  analyzer = '';

  constructor() {
    void this.load();
    const timer = setInterval(() => {
      if (this.hasActive()) void this.load(true);
    }, 5000);
    inject(DestroyRef).onDestroy(() => clearInterval(timer));
  }

  async load(silent = false): Promise<void> {
    if (!silent) this.loading.set(true);
    try {
      const jobs = await firstValueFrom(this.api.videos());
      this.jobs.set([...jobs].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)));
    } catch (e) {
      if (!silent) this.notify.error(e);
    } finally {
      this.loading.set(false);
    }
  }

  async register(): Promise<void> {
    this.busy.set(true);
    try {
      const job = await firstValueFrom(
        this.api.registerVideo({ videoId: this.videoId.trim(), videoUri: this.videoUri.trim(), analyzer: this.analyzer.trim() || null }),
      );
      this.notify.ok(`Queued ${job.videoId}`);
      this.videoId = this.videoUri = this.analyzer = '';
      await this.load();
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }

  async showMap(j: VideoJob): Promise<void> {
    try {
      this.map.set(await firstValueFrom(this.api.videoMap(j.videoId)));
      this.mapVideoId.set(j.videoId);
    } catch (e) {
      this.notify.error(e);
    }
  }
}
