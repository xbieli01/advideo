import { HttpClient } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { AuthMe } from './models';

const STORAGE_KEY = 'advideo.apiKey';

/**
 * API key session. The key lives in sessionStorage only (gone when the tab closes) and is sent as
 * X-Api-Key by the interceptor. Roles come from GET /auth/me (admin | reporting).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);

  readonly key = signal<string | null>(readStored());
  readonly me = signal<AuthMe | null>(null);
  readonly isAdmin = computed(() => this.me()?.roles.includes('admin') ?? false);

  /** Validates the key against the API; stores it only when it works. */
  async login(key: string): Promise<AuthMe> {
    this.key.set(key.trim());
    try {
      const me = await firstValueFrom(this.http.get<AuthMe>('/auth/me'));
      this.me.set(me);
      writeStored(this.key());
      return me;
    } catch (e) {
      this.logout();
      throw e;
    }
  }

  /** Re-validates a key stored in this tab (page reload). */
  async restore(): Promise<boolean> {
    if (this.me()) return true;
    if (!this.key()) return false;
    try {
      this.me.set(await firstValueFrom(this.http.get<AuthMe>('/auth/me')));
      return true;
    } catch {
      this.logout();
      return false;
    }
  }

  logout(): void {
    this.key.set(null);
    this.me.set(null);
    writeStored(null);
  }
}

function readStored(): string | null {
  try {
    return sessionStorage.getItem(STORAGE_KEY);
  } catch {
    return null;
  }
}

function writeStored(v: string | null): void {
  try {
    if (v) sessionStorage.setItem(STORAGE_KEY, v);
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    /* storage unavailable: key stays in memory only */
  }
}
