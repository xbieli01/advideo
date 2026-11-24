import { Component } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import en from '../../../public/i18n/en.json';
import languages from '../../../public/i18n/languages.json';
import sk from '../../../public/i18n/sk.json';
import { parseCatalog } from '../shared/catalog';
import { I18nService, TranslatePipe, interpolate } from './i18n.service';

const FILES: Record<string, unknown> = {
  'i18n/languages.json': languages,
  'i18n/en.json': en,
  'i18n/sk.json': sk,
};

function fakeFetch(url: RequestInfo | URL): Promise<Response> {
  const body = FILES[String(url)];
  return Promise.resolve(body ? new Response(JSON.stringify(body), { status: 200 }) : new Response('', { status: 404 }));
}

@Component({ template: `<span>{{ 'Campaigns' | t }}</span><b>{{ 'Queued {id}' | t: { id: 'v1' } }}</b>`, imports: [TranslatePipe] })
class Host {}

describe('I18nService', () => {
  let i18n: I18nService;

  beforeEach(() => {
    localStorage.clear();
    TestBed.configureTestingModule({});
    i18n = TestBed.inject(I18nService);
    i18n.fetchFn = fakeFetch as typeof fetch;
  });

  it('every language file translates every key of en.json', () => {
    const keys = Object.keys(en);
    for (const l of languages) {
      const dict = (l.code === 'sk' ? sk : l.code === 'en' ? en : null) as Record<string, string> | null;
      expect(dict, `no fixture for ${l.code}`).not.toBeNull();
      expect(keys.filter((k) => !(k in dict!))).toEqual([]);
    }
  });

  it('keeps placeholders in translations', () => {
    for (const [k, v] of Object.entries(sk as Record<string, string>)) {
      const ph = (s: string) => (s.match(/\{\w+\}/g) ?? []).sort().join();
      expect(ph(v), k).toBe(ph(k));
    }
  });

  it('loads languages, switches and remembers the choice', async () => {
    await i18n.init();
    expect(i18n.languages().map((l) => l.code)).toEqual(['en', 'sk']);
    expect(i18n.t('Campaigns')).toBe('Campaigns');
    await i18n.use('sk');
    expect(i18n.t('Campaigns')).toBe('Kampane');
    expect(i18n.t('Saved {name}', { name: 'X' })).toBe('Uložené: X');
    expect(localStorage.getItem('advideo.lang')).toBe('sk');
    expect(document.documentElement.lang).toBe('sk');
  });

  it('falls back to English for unknown languages and missing keys', async () => {
    await i18n.init();
    await i18n.use('xx');
    expect(i18n.lang()).toBe('en');
    await i18n.use('sk');
    expect(i18n.t('Some brand new text')).toBe('Some brand new text');
  });

  it('starts with the saved language', async () => {
    localStorage.setItem('advideo.lang', 'sk');
    await i18n.init();
    expect(i18n.lang()).toBe('sk');
  });

  it('normalizes whitespace in keys (wrapped template texts)', async () => {
    await i18n.init();
    await i18n.use('sk');
    expect(i18n.t('Analyze   a\n video')).toBe('Analyzovať video');
  });

  it('pipe re-renders on language switch', async () => {
    await i18n.init();
    const f = TestBed.createComponent(Host);
    f.detectChanges();
    expect(f.nativeElement.textContent).toContain('Campaigns');
    await i18n.use('sk');
    f.detectChanges();
    expect(f.nativeElement.querySelector('span').textContent).toBe('Kampane');
    expect(f.nativeElement.querySelector('b').textContent).toBe('Zaradené: v1');
  });

  it('interpolate leaves unknown placeholders', () => {
    expect(interpolate('{a} and {b}', { a: 1 })).toBe('1 and {b}');
    expect(interpolate('{n}', { n: 0 })).toBe('0');
  });

  it('translates catalogue parse errors', async () => {
    await i18n.init();
    await i18n.use('sk');
    expect(parseCatalog('broken', (k, p) => i18n.t(k, p)).errors).toEqual(['riadok 1: očakávané videoId,videoUri']);
  });
});
