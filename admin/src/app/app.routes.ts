import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth.guard';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage) },
  {
    path: '',
    canActivate: [authGuard],
    loadComponent: () => import('./pages/shell.component').then((m) => m.ShellComponent),
    children: [
      { path: '', loadComponent: () => import('./pages/overview.page').then((m) => m.OverviewPage), title: 'Overview · advideo' },
      { path: 'campaigns', loadComponent: () => import('./pages/campaigns.page').then((m) => m.CampaignsPage), title: 'Campaigns · advideo' },
      { path: 'campaigns/:id', loadComponent: () => import('./pages/campaign-edit.page').then((m) => m.CampaignEditPage), title: 'Campaign · advideo' },
      { path: 'platforms', loadComponent: () => import('./pages/platforms.page').then((m) => m.PlatformsPage), title: 'Platforms · advideo' },
      { path: 'platforms/:id', loadComponent: () => import('./pages/platform-edit.page').then((m) => m.PlatformEditPage), title: 'Platform · advideo' },
      { path: 'videos', canActivate: [adminGuard], loadComponent: () => import('./pages/videos.page').then((m) => m.VideosPage), title: 'Videos · advideo' },
      { path: 'explain', canActivate: [adminGuard], loadComponent: () => import('./pages/explain.page').then((m) => m.ExplainPage), title: 'Explain · advideo' },
    ],
  },
  { path: '**', redirectTo: '' },
];
