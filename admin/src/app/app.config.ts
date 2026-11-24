import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { TitleStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { routes } from './app.routes';
import { apiKeyInterceptor } from './core/api-key.interceptor';
import { I18nService, I18nTitleStrategy } from './core/i18n.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([apiKeyInterceptor])),
    // UI texts from public/i18n/<lang>.json, loaded before the first render
    provideAppInitializer(() => inject(I18nService).init()),
    { provide: TitleStrategy, useClass: I18nTitleStrategy },
  ],
};
