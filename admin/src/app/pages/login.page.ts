import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCardModule } from '@angular/material/card';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { ActivatedRoute, Router } from '@angular/router';
import { AuthService } from '../core/auth.service';
import { errorMessage } from '../shared/util';

@Component({
  selector: 'adv-login',
  imports: [FormsModule, MatCardModule, MatFormFieldModule, MatInputModule, MatButtonModule],
  template: `
    <div class="login-wrap">
      <mat-card class="login-card">
        <mat-card-header><mat-card-title>advideo admin</mat-card-title></mat-card-header>
        <mat-card-content>
          <p class="muted">Sign in with your API key (admin or reporting).</p>
          <form (ngSubmit)="submit()">
            <mat-form-field appearance="outline" class="full">
              <mat-label>API key</mat-label>
              <input matInput type="password" name="key" [(ngModel)]="key" autocomplete="off" required autofocus />
            </mat-form-field>
            @if (error()) { <p class="error" role="alert">{{ error() }}</p> }
            <button mat-flat-button type="submit" [disabled]="busy() || !key.trim()">Sign in</button>
          </form>
          <p class="muted small">The key is kept only in this browser tab (session storage).</p>
        </mat-card-content>
      </mat-card>
    </div>
  `,
})
export class LoginPage {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);

  key = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);

  async submit(): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.auth.login(this.key);
      const returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') || '/';
      await this.router.navigateByUrl(returnUrl);
    } catch (e) {
      this.error.set(errorMessage(e).startsWith('401') ? 'Invalid API key.' : errorMessage(e));
    } finally {
      this.busy.set(false);
    }
  }
}
