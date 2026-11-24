#!/usr/bin/env node
// Collects UI translation keys from src/app and keeps public/i18n/*.json in sync.
//   node scripts/i18n-extract.mjs          -> rewrite en.json (identity), add missing keys to other languages
//                                             (value = English, marked in a report) and list unused keys
//   node scripts/i18n-extract.mjs --check  -> exit 1 if en.json is stale or a language misses keys
import { readFileSync, writeFileSync, readdirSync, statSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const srcDir = join(root, 'src/app');
const i18nDir = join(root, 'public/i18n');
const check = process.argv.includes('--check');
const norm = (k) => k.replace(/\\'/g, "'").replace(/\s+/g, ' ').trim();

function files(dir) {
  return readdirSync(dir).flatMap((n) => {
    const p = join(dir, n);
    return statSync(p).isDirectory() ? files(p) : p.endsWith('.ts') && !p.endsWith('.spec.ts') ? [p] : [];
  });
}

const keys = new Set();
const lit = String.raw`'((?:[^'\\]|\\.)*)'`;
const patterns = [new RegExp(lit + String.raw`\s*\|\s*t\b`, 'g'), new RegExp(String.raw`\bt\(\s*` + lit, 'g')];
for (const f of files(srcDir)) {
  const s = readFileSync(f, 'utf8');
  if (f.endsWith('i18n-keys.ts')) {
    for (const m of s.matchAll(new RegExp(lit, 'g'))) keys.add(norm(m[1]));
    continue;
  }
  for (const re of patterns) for (const m of s.matchAll(re)) keys.add(norm(m[1]));
}
const sorted = [...keys].filter(Boolean).sort((a, b) => a.localeCompare(b));
const en = Object.fromEntries(sorted.map((k) => [k, k]));
const enPath = join(i18nDir, 'en.json');
const enOld = readFileSync(enPath, 'utf8');
const enNew = JSON.stringify(en, null, 2) + '\n';
let problems = 0;
if (enOld !== enNew) {
  if (check) { console.error('en.json is out of date (run npm run i18n:extract)'); problems++; }
  else writeFileSync(enPath, enNew);
}
const langs = JSON.parse(readFileSync(join(i18nDir, 'languages.json'), 'utf8')).map((l) => l.code).filter((c) => c !== 'en');
for (const code of langs) {
  const p = join(i18nDir, `${code}.json`);
  const dict = JSON.parse(readFileSync(p, 'utf8'));
  const missing = sorted.filter((k) => !(k in dict));
  const unused = Object.keys(dict).filter((k) => !keys.has(k));
  if (missing.length) {
    problems += check ? 1 : 0;
    console.error(`${code}: ${missing.length} missing key(s):\n  ` + missing.join('\n  '));
  }
  if (unused.length) console.warn(`${code}: ${unused.length} unused key(s):\n  ` + unused.join('\n  '));
}
console.log(`${sorted.length} keys, languages: en, ${langs.join(', ')}`);
process.exit(problems ? 1 : 0);
