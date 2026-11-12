import { HttpErrorResponse } from '@angular/common/http';
import { errorMessage, fromList, isoToLocalInput, localInputToIso, toList, toNumberOrNull } from './util';

describe('util', () => {
  it('toList splits on commas and newlines, trims and dedupes', () => {
    expect(toList(' a, b\nc,, a ')).toEqual(['a', 'b', 'c']);
    expect(toList('')).toEqual([]);
    expect(toList(null)).toEqual([]);
  });

  it('fromList joins', () => {
    expect(fromList(['a', 'b'])).toBe('a, b');
    expect(fromList(undefined)).toBe('');
  });

  it('toNumberOrNull', () => {
    expect(toNumberOrNull('')).toBeNull();
    expect(toNumberOrNull(null)).toBeNull();
    expect(toNumberOrNull('abc')).toBeNull();
    expect(toNumberOrNull('12.5')).toBe(12.5);
    expect(toNumberOrNull(0)).toBe(0);
  });

  it('local datetime input round-trips to the same instant', () => {
    const iso = '2026-12-01T09:30:00.000Z';
    const local = isoToLocalInput(iso);
    expect(local).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}$/);
    expect(localInputToIso(local)).toBe(iso);
    expect(isoToLocalInput(null)).toBe('');
    expect(localInputToIso('')).toBeNull();
  });

  it('errorMessage prefers API validation errors', () => {
    const e = new HttpErrorResponse({ status: 400, error: { errors: ['name is required', 'bad bid'] } });
    expect(errorMessage(e)).toBe('name is required · bad bid');
    expect(errorMessage(new HttpErrorResponse({ status: 404, error: { error: 'not found' } }))).toBe('not found');
    expect(errorMessage(new HttpErrorResponse({ status: 403 }))).toContain('admin');
    expect(errorMessage(new HttpErrorResponse({ status: 0 }))).toContain('not reachable');
    expect(errorMessage(new Error('boom'))).toBe('boom');
  });
});
