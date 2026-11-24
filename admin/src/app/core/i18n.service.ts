import { Component, Injectable, Pipe, PipeTransform, computed, effect, inject, signal } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';

/** Entry of public/i18n/languages.json — the list shown in the language selector. */
export interface UiLanguage {
  code: string;
  name: string;
}

export type Dictionary = Record<string, string>;
export type Params = Record<string, string | number | null | undefined>;

export const FALLBACK_LANG = 'en';
const STORAGE_KEY = 'advideo.lang';

/**
 * Runtime UI translations from JSON files (`public/i18n/<code>.json`, served under the app's base href).
 *
 * Keys are the English source texts ("Campaigns", "Queued {n} video(s)"), so a missing translation shows English
 * instead of a cryptic key. `{name}` placeholders are filled from params. Adding a language = add `<code>.json`
 * and an entry in `languages.json`; no rebuild of the app is needed (files are fetched at start-up).
 */
@Injectable({ providedIn: 'root' })
export class I18nService {
  private readonly dicts = signal<Record<string, Dictionary>>({});
  readonly lang = signal<string>(FALLBACK_LANG);
  readonly languages = signal<UiLanguage[]>([{ code: FALLBACK_LANG, name: 'English' }]);
  /** Bumps whenever the active dictionary changes (pipes/computed read it). */
  readonly dict = computed(() => this.dicts()[this.lang()] ?? {});
  private readonly fallback = computed(() => this.dicts()[FALLBACK_LANG] ?? {});

  /** Replaceable in tests. */
  fetchFn: typeof fetch = (...a) => fetch(...a);

  /** Load the language list, the fallback dictionary and the preferred language (app initializer). */
  async init(baseUrl = 'i18n/'): Promise<void> {
    this.baseUrl = baseUrl;
    const list = await this.fetchJson<UiLanguage[]>(`${baseUrl}languages.json`);
    if (Array.isArray(list) && list.length) this.languages.set(list);
    await this.ensure(FALLBACK_LANG);
    const preferred = this.preferred();
    await this.use(preferred);
  }

  private baseUrl = 'i18n/';

  /** Switch language (loads its JSON once); unknown/failed languages fall back to English. */
  async use(code: string): Promise<void> {
    const lang = this.languages().some((l) => l.code === code) ? code : FALLBACK_LANG;
    await this.ensure(lang);
    this.lang.set(this.dicts()[lang] ? lang : FALLBACK_LANG);
    try {
      localStorage.setItem(STORAGE_KEY, this.lang());
    } catch {
      /* storage unavailable: choice lasts for this page only */
    }
    if (typeof document !== 'undefined') document.documentElement.lang = this.lang();
  }

  /** Translate `key` (English source text) and fill `{placeholders}`. */
  t(key: string, params?: Params): string {
    key = normalizeKey(key);
    const text = this.dict()[key] ?? this.fallback()[key] ?? key;
    return params ? interpolate(text, params) : text;
  }

  /** For tests and tooling: install a dictionary directly. */
  register(code: string, dict: Dictionary): void {
    this.dicts.update((d) => ({ ...d, [code]: dict }));
  }

  private preferred(): string {
    try {
      const saved = localStorage.getItem(STORAGE_KEY);
      if (saved) return saved;
    } catch {
      /* ignore */
    }
    const nav = typeof navigator !== 'undefined' ? navigator.language?.slice(0, 2).toLowerCase() : '';
    return nav && this.languages().some((l) => l.code === nav) ? nav : FALLBACK_LANG;
  }

  private async ensure(code: string): Promise<void> {
    if (this.dicts()[code]) return;
    const dict = await this.fetchJson<Dictionary>(`${this.baseUrl}${code}.json`);
    if (dict && typeof dict === 'object') this.register(code, dict);
  }

  private async fetchJson<T>(url: string): Promise<T | null> {
    try {
      const res = await this.fetchFn(url, { cache: 'no-cache' });
      return res.ok ? ((await res.json()) as T) : null;
    } catch {
      return null;
    }
  }
}

/** Keys are compared with collapsed whitespace (templates may wrap long texts). */
export function normalizeKey(key: string): string {
  return key.replace(/\s+/g, ' ').trim();
}

export function interpolate(text: string, params: Params): string {
  return text.replace(/\{(\w+)\}/g, (m, name: string) => (params[name] != null ? String(params[name]) : m));
}

/** `{{ 'Campaigns' | t }}`, `{{ 'Queued {n} video(s)' | t: { n: count } }}` — re-evaluates on language change. */
@Pipe({ name: 't', pure: false })
export class TranslatePipe implements PipeTransform {
  private readonly i18n = inject(I18nService);

  transform(key: string | null | undefined, params?: Params): string {
    return key == null ? '' : this.i18n.t(key, params);
  }
}

/** Route `title`s are translation keys; the document title follows language switches. */
@Injectable({ providedIn: 'root' })
export class I18nTitleStrategy extends TitleStrategy {
  private readonly i18n = inject(I18nService);
  private readonly title = inject(Title);
  private readonly current = signal<string | undefined>(undefined);

  constructor() {
    super();
    effect(() => {
      const key = this.current();
      this.i18n.dict(); // re-run on language change
      this.title.setTitle(key ? `${this.i18n.t(key)} · advideo` : 'advideo admin');
    });
  }

  override updateTitle(snapshot: RouterStateSnapshot): void {
    this.current.set(this.buildTitle(snapshot));
  }
}

/** Compact language switcher (toolbar / login). */
@Component({
  selector: 'adv-lang-select',
  template: `
    <select class="lang-select" [attr.aria-label]="'Language' | t" [value]="i18n.lang()" (change)="switch($event)">
      @for (l of i18n.languages(); track l.code) { <option [value]="l.code" [selected]="l.code === i18n.lang()">{{ l.name }}</option> }
    </select>
  `,
  imports: [TranslatePipe],
})
export class LanguageSelectComponent {
  protected readonly i18n = inject(I18nService);

  switch(e: Event): void {
    void this.i18n.use((e.target as HTMLSelectElement).value);
  }
}
