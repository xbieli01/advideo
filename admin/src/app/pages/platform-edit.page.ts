import { Component, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { Router, RouterLink } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { ApiService } from '../core/api.service';
import { AuthService } from '../core/auth.service';
import { PRICING_MODELS, SLOT_POSITIONS } from '../core/models';
import { NotifyService } from '../shared/notify.service';
import { PlatformForm, formToPlatform, newPlatformForm, platformToForm } from '../shared/platform-form';

@Component({
  selector: 'adv-platform-edit',
  imports: [FormsModule, RouterLink, MatCardModule, MatFormFieldModule, MatInputModule, MatSelectModule, MatCheckboxModule, MatButtonModule],
  template: `
    <div class="page-head">
      <h1>{{ isNew() ? 'New platform' : f.name || f.id }}</h1>
      <a mat-button routerLink="/platforms">Back to list</a>
    </div>
    <form #form="ngForm" (ngSubmit)="save()">
      <fieldset [disabled]="!auth.isAdmin()" class="plain">
        <mat-card class="section"><mat-card-header><mat-card-title>Basics</mat-card-title></mat-card-header>
          <mat-card-content class="grid">
            <mat-form-field appearance="outline"><mat-label>Id</mat-label><input matInput name="id" [(ngModel)]="f.id" required pattern="[a-z0-9][a-z0-9-_.]*" [readonly]="!isNew()" /><mat-hint>used by the snippet (data-platform)</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Name</mat-label><input matInput name="name" [(ngModel)]="f.name" required /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Kind</mat-label>
              <mat-select name="kind" [(ngModel)]="f.kind" [disabled]="!auth.isAdmin()"><mat-option value="portal">portal</mat-option><mat-option value="browserExtension">browser extension</mat-option></mat-select></mat-form-field>
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Pricing</mat-card-title></mat-card-header>
          <mat-card-content>
            <div class="row">
              @for (m of models; track m) {
                <mat-checkbox [name]="'model_' + m" [(ngModel)]="f.enabledModels[m]" [disabled]="!auth.isAdmin()">{{ m.toUpperCase() }}</mat-checkbox>
              }
            </div>
            <div class="grid top-gap">
              <mat-form-field appearance="outline"><mat-label>Currency</mat-label><input matInput name="currency" [(ngModel)]="f.currency" maxlength="3" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Default CTR</mat-label><input matInput type="number" step="0.0001" min="0" max="1" name="ctr" [(ngModel)]="f.defaultCtr" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Default viewable rate</mat-label><input matInput type="number" step="0.05" min="0" max="1" name="vr" [(ngModel)]="f.defaultViewableRate" /></mat-form-field>
              <mat-form-field appearance="outline"><mat-label>Flat sponsorship eCPM</mat-label><input matInput type="number" min="0" name="flat" [(ngModel)]="f.flatSponsorshipEcpm" /></mat-form-field>
            </div>
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Ad slots</mat-card-title></mat-card-header>
          <mat-card-content>
            @for (s of f.slots; track $index) {
              <div class="row">
                <mat-form-field appearance="outline"><mat-label>Id</mat-label><input matInput [name]="'slotId' + $index" [(ngModel)]="s.id" /></mat-form-field>
                <mat-form-field appearance="outline"><mat-label>Position</mat-label>
                  <mat-select [name]="'slotPos' + $index" [(ngModel)]="s.position" [disabled]="!auth.isAdmin()">@for (p of positions; track p) { <mat-option [value]="p">{{ p }}</mat-option> }</mat-select></mat-form-field>
                <mat-form-field appearance="outline" class="narrow"><mat-label>Width</mat-label><input matInput type="number" [name]="'slotW' + $index" [(ngModel)]="s.width" /></mat-form-field>
                <mat-form-field appearance="outline" class="narrow"><mat-label>Height</mat-label><input matInput type="number" [name]="'slotH' + $index" [(ngModel)]="s.height" /></mat-form-field>
                @if (auth.isAdmin()) { <button mat-button type="button" (click)="f.slots.splice($index, 1)">Remove</button> }
              </div>
            }
            @if (auth.isAdmin()) { <button mat-stroked-button type="button" (click)="f.slots.push({ id: '', position: 'below', width: 728, height: 90 })">Add slot</button> }
          </mat-card-content>
        </mat-card>

        <mat-card class="section"><mat-card-header><mat-card-title>Frequency, brand safety, matching</mat-card-title></mat-card-header>
          <mat-card-content class="grid">
            <mat-form-field appearance="outline"><mat-label>Min interval (s)</mat-label><input matInput type="number" min="0" name="minInt" [(ngModel)]="f.minIntervalSec" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Max ads per video</mat-label><input matInput type="number" min="0" name="maxV" [(ngModel)]="f.maxPerVideo" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Max per viewer / day</mat-label><input matInput type="number" min="0" name="maxD" [(ngModel)]="f.maxPerViewerPerDay" /><mat-hint>empty = no limit</mat-hint></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclude categories</mat-label><input matInput name="exCat" [(ngModel)]="f.excludeCategories" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Exclude keywords</mat-label><input matInput name="exKw" [(ngModel)]="f.excludeKeywords" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Relevance threshold</mat-label><input matInput type="number" step="0.05" min="0" max="1" name="thr" [(ngModel)]="f.relevanceThreshold" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Weight brand</mat-label><input matInput type="number" step="0.1" name="wb" [(ngModel)]="f.wBrand" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Weight entity</mat-label><input matInput type="number" step="0.1" name="we" [(ngModel)]="f.wEntity" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Weight keyword</mat-label><input matInput type="number" step="0.1" name="wk" [(ngModel)]="f.wKeyword" /></mat-form-field>
            <mat-form-field appearance="outline"><mat-label>Weight category</mat-label><input matInput type="number" step="0.1" name="wc" [(ngModel)]="f.wCategory" /></mat-form-field>
          </mat-card-content>
        </mat-card>
      </fieldset>
      @if (auth.isAdmin()) {
        <div class="form-actions">
          <button mat-flat-button type="submit" [disabled]="busy() || form.invalid">{{ isNew() ? 'Create' : 'Save' }}</button>
          <a mat-button routerLink="/platforms">Cancel</a>
        </div>
      } @else {
        <p class="muted">Read only — your API key has the reporting role.</p>
      }
    </form>
  `,
})
export class PlatformEditPage {
  private readonly api = inject(ApiService);
  private readonly router = inject(Router);
  private readonly notify = inject(NotifyService);
  protected readonly auth = inject(AuthService);

  readonly id = input<string>('new');
  readonly models = PRICING_MODELS;
  readonly positions = SLOT_POSITIONS;
  readonly isNew = signal(true);
  readonly busy = signal(false);
  f: PlatformForm = newPlatformForm();

  async ngOnInit(): Promise<void> {
    const id = this.id();
    if (!id || id === 'new') return;
    this.isNew.set(false);
    try {
      this.f = platformToForm(await firstValueFrom(this.api.platform(id)));
    } catch (e) {
      this.notify.error(e);
    }
  }

  async save(): Promise<void> {
    this.busy.set(true);
    try {
      const p = formToPlatform(this.f);
      const saved = await firstValueFrom(this.isNew() ? this.api.createPlatform(p) : this.api.updatePlatform(p));
      this.notify.ok(`Saved ${saved.name}`);
      await this.router.navigate(['/platforms']);
    } catch (e) {
      this.notify.error(e);
    } finally {
      this.busy.set(false);
    }
  }
}
