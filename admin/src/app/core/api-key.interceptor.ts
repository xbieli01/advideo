import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { AuthService } from './auth.service';

export const API_KEY_HEADER = 'X-Api-Key';

/** Adds the API key to same-origin API calls; on 401 drops the session and goes to the login page. */
export const apiKeyInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);
  const key = auth.key();
  const sameOrigin = req.url.startsWith('/');
  const request = key && sameOrigin ? req.clone({ setHeaders: { [API_KEY_HEADER]: key } }) : req;

  return next(request).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && sameOrigin && !req.url.startsWith('/auth/me')) {
        auth.logout();
        void router.navigate(['/login']);
      }
      return throwError(() => err);
    }),
  );
};
