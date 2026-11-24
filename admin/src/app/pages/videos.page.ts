import { DatePipe, JsonPipe } from '@angular/common';
import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { I18nService, TranslatePipe } from '../core/i18n.service';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatExpansionModule } from '@angular/material/expansion';
import { MatSelectModule } from '@angular/material/select';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatTableModule } from '@angular/material/table';
import { RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { BulkRegisterResponse, VideoJob } from '../core/models';
import { langs, parseCatalog } from '../shared/catalog';
import { NotifyService } from '../shared/notify.service';

const ACTIVE = ['queued', 'running', 'retrying'];
const STATUSES = ['Queued', 'Running', 'Retrying', 'Completed', 'Failed'];

@Component({
  selector: 'adv-videos',
  imports: [TranslatePipe, FormsModule, RouterLink, DatePipe, JsonPipe, MatCardModule, MatTableModule, MatButtonModule, MatCheckboxModule, MatExpansionModule, MatSelectModule, MatFormFieldModule, MatInputModule, MatProgressBarModule],
  template: `
    <div class="page-head">
      <h1>{{ 'Videos' | t }}</h1>
      <button mat-stroked-button (click)="load()">{{ 'Refresh' | t }}</button>
    </div>
    <mat-card class="section">
      <mat-card-header><mat-card-title>{{ 'Analyze a video' | t }}</mat-card-title><mat-card-subtitle>{{ 'creates the content map used for ad matching' | t }}</mat-card-subtitle></mat-card-header>
      <mat-card-content>
        <form #form="ngForm" class="row" (ngSubmit)="register()">
          <mat-form-field appearance="outline"><mat-label>{{ 'Video id' | t }}</mat-label><input matInput name="videoId" [(ngModel)]="videoId" required /></mat-form-field>
          <mat-form-field appearance="outline" class="wide"><mat-label>{{ 'Video URI' | t }}</mat-label><input matInput name="videoUri" [(ngModel)]="videoUri" required placeholder="/videos/demo.mp4, https://cdn/…/video.mp4 or …/index.m3u8" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>{{ 'Analyzer' | t }}</mat-label><input matInput name="analyzer" [(ngModel)]="analyzer" placeholder="yolo+clip" /><mat-hint>{{ 'empty = default' | t }}</mat-hint></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>{{ 'Languages' | t }}</mat-label><input matInput name="languages" [(ngModel)]="languages" placeholder="sk,cs,en" /><mat-hint>{{ 'speech/OCR language packs; empty = default' | t }}</mat-hint></mat-form-field>
          <button mat-flat-button type="submit" [disabled]="busy() || form.invalid">{{ 'Queue analysis' | t }}</button>
        </form>
      </mat-card-content>
    </mat-card>
    <mat-accordion class="section">
      <mat-expansion-panel>
        <mat-expansion-panel-header>
          <mat-panel-title>{{ 'Import catalogue' | t }}</mat-panel-title>
          <mat-panel-description>{{ 'many videos at once (CSV or JSON, max 1000 per import)' | t }}</mat-panel-description>
        </mat-expansion-panel-header>
        <p class="muted small">{{ 'CSV: one video per line' | t }} <code>videoId,videoUri[,analyzer[,track[,languages]]]</code> {{ '(header optional, ; or tab also work) — or JSON' | t }}
          <code>[&#123;"videoId": "…", "videoUri": "…"&#125;]</code>. {{ 'Videos already analyzed from the same URI are skipped unless “Re-analyze” is checked.' | t }}</p>
        <div class="row">
          <input type="file" accept=".csv,.tsv,.txt,.json" (change)="loadFile($event)" />
        </div>
        <mat-form-field appearance="outline" class="full">
          <mat-label>{{ 'Catalogue' | t }}</mat-label>
          <textarea matInput rows="6" [(ngModel)]="catalogText" (ngModelChange)="catalogResult.set(null)" placeholder="movie-001,https://cdn.example.com/movie-001.mp4"></textarea>
          <mat-hint>{{ '{n} video(s) recognised' | t: { n: parsed().items.length } }}{{ parsed().errors.length ? ' · ' + ('{n} line(s) with errors' | t: { n: parsed().errors.length }) : '' }}</mat-hint>
        </mat-form-field>
        @for (e of parsed().errors.slice(0, 5); track e) { <div class="error small">{{ e }}</div> }
        <div class="row">
          <mat-form-field appearance="outline"><mat-label>{{ 'Default analyzer' | t }}</mat-label><input matInput [(ngModel)]="bulkAnalyzer" placeholder="yolo+clip" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>{{ 'Default track' | t }}</mat-label><input matInput [(ngModel)]="bulkTrack" [placeholder]="'whisper' | t" /></mat-form-field>
          <mat-form-field appearance="outline"><mat-label>{{ 'Default languages' | t }}</mat-label><input matInput [(ngModel)]="bulkLanguages" placeholder="sk,cs,en" /></mat-form-field>
          <mat-checkbox [(ngModel)]="bulkForce">{{ 'Re-analyze already analyzed videos' | t }}</mat-checkbox>
          <button mat-flat-button (click)="importCatalog()" [disabled]="busy() || parsed().items.length === 0 || parsed().items.length > 1000">{{ 'Import' | t }} {{ parsed().items.length || '' }}</button>
        </div>
        @if (catalogResult(); as r) {
          <p>{{ '{queued} queued · {skipped} skipped · {invalid} invalid (of {received})' | t: { queued: r.queued, skipped: r.skipped.length, invalid: r.invalid.length, received: r.received } }}</p>
          @for (x of r.invalid.slice(0, 10); track x.index) { <div class="error small">#{{ x.index + 1 }} {{ x.videoId }}: {{ x.reason }}</div> }
          @for (x of r.skipped.slice(0, 10); track x.index) { <div class="muted small">#{{ x.index + 1 }} {{ x.videoId }}: {{ x.reason }}</div> }
        }
      </mat-expansion-panel>
    </mat-accordion>
    <div class="row">
      <mat-form-field appearance="outline">
        <mat-label>{{ 'Status' | t }}</mat-label>
        <mat-select [(ngModel)]="statusFilter" (selectionChange)="load()">
          <mat-option [value]="''">{{ 'All' | t }}</mat-option>
          @for (st of statuses; track st) { <mat-option [value]="st">{{ (st === 'Failed' ? 'Failed (dead letter)' : st) | t }}</mat-option> }
        </mat-select>
      </mat-form-field>
    </div>
    @if (loading()) { <mat-progress-bar mode="indeterminate" /> }
    @if (hasActive()) { <p class="muted small">{{ 'Jobs in progress — refreshing every 5 s.' | t }}</p> }
    <table mat-table [dataSource]="jobs()" class="full">
      <ng-container matColumnDef="video">
        <th mat-header-cell *matHeaderCellDef>{{ 'Video' | t }}</th>
        <td mat-cell *matCellDef="let j">{{ j.videoId }}<div class="muted small">{{ j.videoUri }}</div></td>
      </ng-container>
      <ng-container matColumnDef="analyzer">
        <th mat-header-cell *matHeaderCellDef>{{ 'Analyzer' | t }}</th>
        <td mat-cell *matCellDef="let j">{{ j.analyzer }}@if (j.languages) { <div class="muted small">{{ j.languages }}</div> }</td>
      </ng-container>
      <ng-container matColumnDef="status">
        <th mat-header-cell *matHeaderCellDef>{{ 'Status' | t }}</th>
        <td mat-cell *matCellDef="let j"><span [class]="'status ' + j.status.toLowerCase()">{{ j.status | t }}</span>
          @if (j.attempts > 1 || j.status === 'Retrying') { <span class="muted small"> · {{ 'attempt {n}' | t: { n: j.attempts } }}</span> }
          @if (j.status === 'Retrying' && j.nextAttemptAt) { <div class="muted small">{{ 'next try {time}' | t: { time: (j.nextAttemptAt | date: 'shortTime') } }}</div> }
          @if (j.error) { <div class="error small">{{ j.error }}</div> }</td>
      </ng-container>
      <ng-container matColumnDef="updated">
        <th mat-header-cell *matHeaderCellDef>{{ 'Updated' | t }}</th>
        <td mat-cell *matCellDef="let j">{{ j.updatedAt | date: 'short' }}</td>
      </ng-container>
      <ng-container matColumnDef="actions">
        <th mat-header-cell *matHeaderCellDef></th>
        <td mat-cell *matCellDef="let j" class="actions">
          @if (j.status === 'Failed') { <button mat-button color="warn" (click)="retry(j)" [disabled]="busy()">{{ 'Retry' | t }}</button> }
          @if (j.mapKey) {
            <button mat-button (click)="showMap(j)">{{ 'Content map' | t }}</button>
            <a mat-button routerLink="/explain" [queryParams]="{ videoId: j.videoId }">{{ 'Explain' | t }}</a>
          }
        </td>
      </ng-container>
      <tr mat-header-row *matHeaderRowDef="cols"></tr>
      <tr mat-row *matRowDef="let row; columns: cols"></tr>
    </table>
    @if (!loading() && jobs().length === 0) { <p class="muted">{{ statusFilter ? ('No videos with status {status}.' | t: { status: (statusFilter | t) }) : ('No videos analyzed yet.' | t) }}</p> }
    @if (map(); as m) {
      <mat-card class="section">
        <mat-card-header><mat-card-title>{{ 'Content map' | t }} · {{ mapVideoId() }}</mat-card-title></mat-card-header>
        <mat-card-content>
          <pre class="json">{{ m | json }}</pre>
          <button mat-button (click)="map.set(null)">{{ 'Close' | t }}</button>
        </mat-card-content>
      </mat-card>
    }
  `,
})
export class VideosPage {
  private readonly api = inject(ApiService);
  private readonly notify = inject(NotifyService);
  protected readonly i18n = inject(I18nService);

  readonly cols = ['video', 'analyzer', 'status', 'updated', 'actions'];
  readonly jobs = signal<VideoJob[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly map = signal<unknown>(null);
  readonly mapVideoId = signal('');
  readonly hasActive = computed(() => this.jobs().some((j) => ACTIVE.includes(j.status.toLowerCase())));
  readonly statuses = STATUSES;
  readonly catalogResult = signal<BulkRegisterResponse | null>(null);
  private readonly catalog = signal('');
  readonly parsed = computed(() => parseCatalog(this.catalog(), (k, prm) => this.i18n.t(k, prm)));
  videoId = '';
  videoUri = '';
  analyzer = '';
  languages = '';
  statusFilter = '';
  bulkAnalyzer = '';
  bulkTrack = '';
  bulkLanguages = '';
  bulkForce = false;

  get catalogText(): string {
    return this.catalog();
  }
  set catalogText(v: string) {
    this.catalog.set(v ?? '');
  }

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
      const jobs = await firstValueFrom(this.api.videos(this.statusFilter || null));
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
        this.api.registerVideo({ videoId: this.videoId.trim(), videoUri: this.videoUri.trim(), analyzer: this.analyzer.trim() || null, languages: langs(this.languages) }),
      );
      this.notify.ok(this.i18n.t('Queued {id}', { id: job.videoId }));
      this.videoId = this.videoUri = this.analyzer = this.languages = '';
      await this.load();
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }

  async retry(j: VideoJob): Promise<void> {
    this.busy.set(true);
    try {
      await firstValueFrom(this.api.retryVideo(j.videoId));
      this.notify.ok(this.i18n.t('Re-queued {id}', { id: j.videoId }));
      await this.load();
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }

  async loadFile(event: Event): Promise<void> {
    const file = (event.target as HTMLInputElement).files?.[0];
    if (!file) return;
    this.catalogText = await file.text();
    this.catalogResult.set(null);
  }

  async importCatalog(): Promise<void> {
    const { items } = this.parsed();
    if (items.length === 0) return;
    this.busy.set(true);
    try {
      const r = await firstValueFrom(
        this.api.bulkRegisterVideos({
          items,
          analyzer: this.bulkAnalyzer.trim() || null,
          track: this.bulkTrack.trim() || null,
          languages: langs(this.bulkLanguages),
          force: this.bulkForce,
        }),
      );
      this.catalogResult.set(r);
      this.notify.ok(this.i18n.t('Queued {queued} of {received} video(s)', { queued: r.queued, received: r.received }));
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
