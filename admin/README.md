# advideo admin

Angular 22 + Material SPA for the advideo API (campaigns, platforms, video analysis, decision explain).
Served by the API under `/admin` (base href `/admin/`). See the root README, section "Admin UI".

```bash
npm ci
npx ng serve                 # http://localhost:4200/admin/, API proxied to http://localhost:5000
npx ng test --watch=false    # unit tests (Vitest)
npx ng build                 # dist/admin/ -> copied to the API's wwwroot/admin by the Dockerfile
```

Notes
- Strict CSP (`script-src 'self'`): critical-CSS inlining is disabled in `angular.json`
  because it injects an inline loader script. Don't add inline scripts or external fonts/icons.
- API key lives in sessionStorage (`advideo.apiKey`); a 401 drops it and returns to `/login`.
- UI texts: runtime JSON translations in `public/i18n/` (`languages.json`, `en.json`, `sk.json`); use
  `{{ 'English text' | t }}` / `i18n.t('Saved {name}', { name })` in code. `npm run i18n:extract` updates `en.json`
  and reports missing keys per language, `npm run i18n:check` fails on gaps. Values used only through variables
  (statuses, computed labels) are listed in `src/app/core/i18n-keys.ts`.

