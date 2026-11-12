import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { firstValueFrom } from 'rxjs';
import { apiKeyInterceptor } from './api-key.interceptor';
import { AuthService } from './auth.service';

describe('AuthService + apiKeyInterceptor', () => {
  let auth: AuthService;
  let http: HttpClient;
  let ctl: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([apiKeyInterceptor])), provideHttpClientTesting()],
    });
    auth = TestBed.inject(AuthService);
    http = TestBed.inject(HttpClient);
    ctl = TestBed.inject(HttpTestingController);
  });

  afterEach(() => ctl.verify());

  it('login validates the key via /auth/me and stores it in sessionStorage', async () => {
    const p = auth.login(' secret ');
    const req = ctl.expectOne('/auth/me');
    expect(req.request.headers.get('X-Api-Key')).toBe('secret');
    req.flush({ name: 'ops', roles: ['admin'] });
    await expect(p).resolves.toEqual({ name: 'ops', roles: ['admin'] });
    expect(auth.isAdmin()).toBe(true);
    expect(sessionStorage.getItem('advideo.apiKey')).toBe('secret');
  });

  it('failed login clears the key', async () => {
    const p = auth.login('wrong');
    ctl.expectOne('/auth/me').flush(null, { status: 401, statusText: 'Unauthorized' });
    await expect(p).rejects.toBeTruthy();
    expect(auth.key()).toBeNull();
    expect(sessionStorage.getItem('advideo.apiKey')).toBeNull();
  });

  it('reporting role is not admin', async () => {
    const p = auth.login('r');
    ctl.expectOne('/auth/me').flush({ name: 'rep', roles: ['reporting'] });
    await p;
    expect(auth.isAdmin()).toBe(false);
  });

  it('does not send the key to other origins', () => {
    auth.key.set('k');
    http.get('https://example.com/x').subscribe();
    expect(ctl.expectOne('https://example.com/x').request.headers.has('X-Api-Key')).toBe(false);
  });

  it('401 on an API call logs out and navigates to /login', async () => {
    const router = TestBed.inject(Router);
    const nav = vi.spyOn(router, 'navigate').mockResolvedValue(true);
    auth.key.set('k');
    const p = firstValueFrom(http.get('/campaigns')).catch((e) => e);
    ctl.expectOne('/campaigns').flush(null, { status: 401, statusText: 'Unauthorized' });
    await p;
    expect(auth.key()).toBeNull();
    expect(nav).toHaveBeenCalledWith(['/login']);
  });
});
