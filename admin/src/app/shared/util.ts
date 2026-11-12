import { HttpErrorResponse } from '@angular/common/http';

/** "a, b\nc" -> ["a","b","c"] (trimmed, empty items dropped, duplicates removed). */
export function toList(text: string | null | undefined): string[] {
  if (!text) return [];
  return [...new Set(text.split(/[,\n]/).map((s) => s.trim()).filter((s) => s.length > 0))];
}

export function fromList(list: readonly string[] | null | undefined): string {
  return (list ?? []).join(', ');
}

/** Empty input -> null, otherwise a number (forms deliver strings or numbers). */
export function toNumberOrNull(v: unknown): number | null {
  if (v === null || v === undefined || v === '') return null;
  const n = Number(v);
  return Number.isFinite(n) ? n : null;
}

/** ISO string -> value for <input type="datetime-local"> (local time, minutes). */
export function isoToLocalInput(iso: string | null | undefined): string {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

export function localInputToIso(v: string | null | undefined): string | null {
  if (!v) return null;
  const d = new Date(v);
  return Number.isNaN(d.getTime()) ? null : d.toISOString();
}

/** Human readable message from an API error (ValidationErrorResponse, {error}, or HTTP status). */
export function errorMessage(err: unknown): string {
  if (err instanceof HttpErrorResponse) {
    const body = err.error as { errors?: string[]; error?: string } | string | null;
    if (body && typeof body === 'object') {
      if (Array.isArray(body.errors) && body.errors.length) return body.errors.join(' · ');
      if (typeof body.error === 'string') return body.error;
    }
    if (typeof body === 'string' && body.length < 300 && body.trim()) return body;
    if (err.status === 0) return 'API is not reachable.';
    if (err.status === 403) return 'Not allowed for this API key (admin role required).';
    return `${err.status} ${err.statusText || 'Error'}`;
  }
  return err instanceof Error ? err.message : String(err);
}
