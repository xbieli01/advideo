import { CurrencyPipe, DecimalPipe, PercentPipe } from '@angular/common';
import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { CAMPAIGN_STATUSES, CampaignStats, PRICING_MODELS } from '../core/models';
import { CampaignForm, campaignToForm, formToCampaign, newCampaignForm } from '../shared/campaign-form';
import { NotifyService } from '../shared/notify.service';

@Component({
  selector: 'adv-campaign-edit',
  imports: [FormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatButtonModule, DecimalPipe, PercentPipe, CurrencyPipe],
  template: `
    <div class="page-head">
      <h1>{{ isNew() ? 'New campaign' : f.name || f.id }}</h1>
      <a mat-button routerLink="/campaigns">Back to list</a>
    </div>
    @if (stats(); as s) {
      <div class="kpis">
        <mat-card><mat-card-content><div class="kpi-label">Impressions</div><div class="kpi">{{ s.impressions | number }}</div></mat-card-content></mat-card>
        <mat-card><mat-card-content><div class="kpi-label">Viewable</div><div class="kpi">{{ s.viewables | number }}</div></mat-card-content></mat-card>
        <mat-card><mat-card-content><div class="kpi-label">Clicks · CTR</div><div class="kpi">{{ s.clicks | number }} · {{ s.ctr | percent: '1.2-2' }}</div></mat-card-content></mat-card>
        <mat-card><mat-card-content><div class="kpi-label">Spent today / total</div><div class="kpi">{{ s.spentToday | currency: 'EUR' }} / {{ s.spentTotal | currency: 'EUR' }}</div></mat-card-content></mat-card>
      </div>
    }
    <form #form="ngForm" (ngSubmit)="save()">
      <fieldset [disabled]="!auth.isAdmin()" class="plain">
        <mat-card class="section"><mat-card-header><mat-card-title>Basics</mat-card-title></mat-card-header>
          <mat-card-content class="grid">
            <mat-form-field appearance="outline"><mat-label>Id</mat-label><input matInput name="id" [(ngModel)]="f.id" required pattern="[a-z0-9][a-z0-9-_.]*" [readonly]="!isNew()" /><mat-hint>lowercase, unique</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" required /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Advertiser id</mat-label><input matInput name="advertiserId" [(ngModel)]="f.advertiserId" required /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Status</mat-label>
              <mat-select name="status" [(ngModel)]="f.status" [disabled]="!auth.isAdmin()">@for (s of statuses; track s) { <mat-option [value]="s">{{ s }}</mat-option> }</mat-select></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Start</mat-label><input matInput type="datetime-local" name="startAt" [(ngModel)]="f.startAt" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>End</mat-label><input matInput type="datetime-local" name="endAt" [(ngModel)]="f.endAt" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Priority (1–10)</mat-label><input matInput type="number" min="1" max="10" name="priority" [(ngModel)]="f.priority" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclusivity key</mat-label><input matInput name="exclusivityKey" [(ngModel)]="f.exclusivityKey" /><mat-hint>e.g. automotive</mat-hint></mat-form-field>
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Bids, budget, frequency</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (b of f.bids; track $index) {
              <div class="row">
                <mat-form-field appearance="outline"><mat-label>Model</mat-label>
                  <mat-select [name]="'bidModel' + $index" [(ngModel)]="b.model" [disabled]="!auth.isAdmin()">@for (m of models; track m) { <mat-option [value]="m">{{ m.toUpperCase() }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field appearance="outline"><mat-label>Amount</mat-label><input matInput type="number" step="0.01" min="0" [name]="'bidAmount' + $index" [(ngModel)]="b.amount" /></mat-form-field>
                @if (auth.isAdmin()) { <button mat-button type="button" (click)="f.bids.splice($index, 1)">Remove</button> }
              </div>
            }
            @if (auth.isAdmin()) { <button mat-stroked-button type="button" (click)="f.bids.push({ model: 'cpm', amount: null })">Add bid</button> }
            <div class="grid top-gap">
              <mat-form-field appearance="outline"><mat-label>Budget daily</mat-label><input matInput type="number" min="0" name="budgetDaily" [(ngModel)]="f.budgetDaily" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Budget total</mat-label><input matInput type="number" min="0" name="budgetTotal" [(ngModel)]="f.budgetTotal" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Cap per viewer / video</mat-label><input matInput type="number" min="0" name="capV" [(ngModel)]="f.capPerViewerPerVideo" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Cap per viewer / day</mat-label><input matInput type="number" min="0" name="capD" [(ngModel)]="f.capPerViewerPerDay" /></mat-form-field>
            </div>
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Targeting</mat-card-title><mat-card-subtitle>comma separated</mat-card-subtitle></mat-card-header>
          <mat-card-content class="grid">
            <mat-form-field appearance="outline"><mat-label>Brands</mat-label><input matInput name="brands" [(ngModel)]="f.brands" placeholder="Tesla, Nike" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Keywords</mat-label><input matInput name="keywords" [(ngModel)]="f.keywords" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Categories</mat-label><input matInput name="categories" [(ngModel)]="f.categories" /><mat-hint>taxonomy ids, validated by the API</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Entities (type:label)</mat-label><input matInput name="entities" [(ngModel)]="f.entities" placeholder="object:car" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclude categories</mat-label><input matInput name="excludeCategories" [(ngModel)]="f.excludeCategories" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclude keywords</mat-label><input matInput name="excludeKeywords" [(ngModel)]="f.excludeKeywords" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Min score (0–1)</mat-label><input matInput type="number" step="0.05" min="0" max="1" name="minScore" [(ngModel)]="f.minScore" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Platforms</mat-label><input matInput name="platforms" [(ngModel)]="f.platforms" /><mat-hint>empty = all</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Geo (ISO codes)</mat-label><input matInput name="geo" [(ngModel)]="f.geo" placeholder="SK, CZ" /><mat-hint>empty = all</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Include videos</mat-label><input matInput name="includeVideos" [(ngModel)]="f.includeVideos" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclude videos</mat-label><input matInput name="excludeVideos" [(ngModel)]="f.excludeVideos" /></mat-form-field>
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Creatives</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (c of f.creatives; track $index) {
              <div class="row creative">
                <mat-form-field appearance="outline"><mat-label>Id</mat-label><input matInput [name]="'crId' + $index" [(ngModel)]="c.id" /></mat-form-field>
                <mat-form-field appearance="outline" class="wide"><mat-label>Image URL</mat-label><input matInput [name]="'crUrl' + $index" [(ngModel)]="c.url" /></mat-form-field>
                <mat-form-field appearance="outline" class="wide"><mat-label>Click URL</mat-label><input matInput [name]="'crClick' + $index" [(ngModel)]="c.clickUrl" /></mat-form-field>
                <mat-form-field appearance="outline" class="narrow"><mat-label>W</mat-label><input matInput type="number" [name]="'crW' + $index" [(ngModel)]="c.width" /></mat-form-field>
                <mat-form-field appearance="outline" class="narrow"><mat-label>H</mat-label><input matInput type="number" [name]="'crH' + $index" [(ngModel)]="c.height" /></mat-form-field>
                @if (c.url) { <img class="thumb" [src]="c.url" [alt]="c.altText || c.id" /> }
                @if (auth.isAdmin()) { <button mat-button type="button" (click)="f.creatives.splice($index, 1)">Remove</button> }
              </div>
            }
            @if (auth.isAdmin()) {
              <button mat-stroked-button type="button" (click)="f.creatives.push({ id: '', url: '', clickUrl: '', width: 300, height: 250, altText: null })">Add creative</button>
            }
          </mat-card-content>
        </mat-card>
      </fieldset>
      @if (auth.isAdmin()) {
        <div class="form-actions">
          <button mat-flat-button type="submit" [disabled]="busy() || form.invalid">{{ isNew() ? 'Create' : 'Save' }}</button>
          <a mat-button routerLink="/campaigns">Cancel</a>
        </div>
      } @else {
        <p class="muted">Read only — your API key has the reporting role.</p>
      }
    </form>
  `,
})
export class CampaignEditPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly auth = inject(AuthService);

  /** Route param (component input binding); "new" for a new campaign. */
  readonly id = input<string>('new');
  readonly statuses = CAMPAIGN_STATUSES;
  readonly models = PRICING_MODELS;
  readonly isNew = signal(true);
  readonly busy = signal(false);
  readonly stats = signal<CampaignStats | null>(null);
  f: CampaignForm = newCampaignForm();

  async ngOnInit(): Promise<void> {
    const id = this.id();
    if (!id || id === 'new') return;
    this.isNew.set(false);
    try {
      this.f = campaignToForm(await firstValueFrom(this.api.campaign(id)));
      this.stats.set(await firstValueFrom(this.api.campaignStats(id)));
    } catch (e) {
      this.notify.error(e);
    }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const c = formToCampaign(this.f);
      const saved = await firstValueFrom(this.isNew() ? this.api.createCampaign(c) : this.api.updateCampaign(c));
      this.notify.ok(`Saved ${saved.name}`);
      await this.router.navigate(['/campaigns']);
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }
}
