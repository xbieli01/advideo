# advideo snippet (M3)

Vanilla TypeScript, zero runtime dependencies, ~9 kB minified (~4 kB gzip).
Follows the HTML5 player's time, asks the ad server (`GET /ads/decision`) for the ad matching the current
content segment, renders it as a banner into the platform's slot around the player and reports delivery
events (`POST /ads/events`).

## Embed (customer portal)

```html
<video id="player" src="…"></video>
<div id="ad-below"></div> <div id="ad-right"></div>

<script src="https://cdn.example/advideo.min.js"></script>
<script>
  const ads = Advideo.init({
    apiBase: "https://ads.example.com",
    platform: "customer-portal-id",          // PlatformConfig.id on the server
    videoId: "the-video-id",                 // id of the content map
    video: "#player",
    slots: { "below-player": "#ad-below", "right-of-player": "#ad-right" }, // = platform slot ids
    consent: { mode: "tcf" },                // or { mode: "manual", granted } + ads.setConsent(true|false)
    geo: "SK",
  });
  // SPA: ads.setVideo("next-id");  teardown: ads.destroy();
</script>
```

## Behaviour

| Topic | Rule |
|---|---|
| Requests | only while playing; at most every `minRequestIntervalSec` (5 s); after "no ad" wait `noAdBackoffSec` (15 s) of playback; immediately after a seek |
| Banner | shown from current time to the end of the matched segment (`showToSec`), removed on expiry / seek outside / close; label "Reklama"; `rel="sponsored"`; only http(s) URLs; no innerHTML |
| Impression | when the creative image has loaded |
| Viewable | IAB/MRC: ≥ 50 % of the banner in view for ≥ 1 s while the tab is visible |
| Click / close | tracked; a closed campaign is not shown again in that video |
| Events transport | `navigator.sendBeacon` with `text/plain` JSON (CORS-simple, survives unload), fallback `fetch(keepalive)` |
| Viewer id | random first-party id in `localStorage`, **only with consent** (TCF purpose 1 or `setConsent(true)`), deleted on withdrawal; server keeps only an HMAC; without consent frequency is enforced from page counters |
| Client counters | `adsInVideo`, `lastAdAt`, `prevCampaign` are sent with every request; the server merges them with its own (stricter wins) |

## Develop

```bash
npm install
npm test            # vitest + jsdom (unit)
npm run build       # dist/advideo.min.js (IIFE, window.Advideo) + dist/advideo.esm.js + types
# end-to-end against a running orchestrator (map "golden-mock" stored, sample seed loaded):
ADVIDEO_E2E_API=http://localhost:5219 npx vitest run test/e2e.test.ts
```

Demo page: `demo/index.html`.

Browser extension (universal platform) = M3b: same core, content script attaches to any `<video>`,
video id derived from the page URL, banner in an extension-owned side panel.
