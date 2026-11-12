import { Component, inject } from '@angular/core';
import { MatButtonModule } from '@angular/material/button';
import { MatListModule } from '@angular/material/list';
import { MatSidenavModule } from '@angular/material/sidenav';
import { MatToolbarModule } from '@angular/material/toolbar';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { AuthService } from '../core/auth.service';

@Component({
  selector: 'adv-shell',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, MatToolbarModule, MatSidenavModule, MatListModule, MatButtonModule],
  template: `
    <mat-toolbar class="topbar">
      <span class="brand">advideo admin</span>
      <span class="spacer"></span>
      @if (auth.me(); as me) {
        <span class="who">{{ me.name }} · {{ me.roles.join(', ') }}</span>
      }
      <button mat-button (click)="logout()">Sign out</button>
    </mat-toolbar>
    <mat-sidenav-container class="body">
      <mat-sidenav mode="side" opened class="nav">
        <mat-nav-list>
          <a mat-list-item routerLink="/" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: true }">Overview</a>
          <a mat-list-item routerLink="/campaigns" routerLinkActive="active">Campaigns</a>
          <a mat-list-item routerLink="/platforms" routerLinkActive="active">Platforms</a>
          @if (auth.isAdmin()) {
            <a mat-list-item routerLink="/videos" routerLinkActive="active">Videos</a>
            <a mat-list-item routerLink="/explain" routerLinkActive="active">Explain decision</a>
          }
        </mat-nav-list>
      </mat-sidenav>
      <mat-sidenav-content class="content"><router-outlet /></mat-sidenav-content>
    </mat-sidenav-container>
  `,
})
export class ShellComponent {
  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  logout(): void {
    this.auth.logout();
    void this.router.navigate(['/login']);
  }
}
