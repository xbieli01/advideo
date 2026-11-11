# advideo — M2: ad-serving + matching engine (návrh)

Stav: schválené 2026-11-11 (rozhodnutia v §9) · M2 (T-005a, b, c) implementované · nadväzuje na M1 (`2497f09`)

## 1. Cieľ M2

Pre daný `videoId` a čas prehrávania `t` vrátiť **jednu najlepšiu reklamu** (alebo žiadnu), ktorej
targeting sedí na obsah aktuálneho úseku videa z JSON mapy (M1). Výsledok musí byť:

- rýchly (p95 < 30 ms na serveri, bez volania ML),
- vysvetliteľný (prečo táto reklama — pre inzerenta aj debugging),
- testovateľný bez GPU (mapy aj kampane ako fixtures).

Mimo M2: skutočná aukcia v reálnom čase (RTB), billing, UI pre inzerentov, JS snippet (M3).

## 2. Umiestnenie v architektúre

Nový projekt v existujúcom .NET riešení (ADR-001 — serving je na .NET strane):

```
orchestrator/src/
  Advideo.Orchestrator.Core          (+ Model/Ads, Abstractions/ICampaignRepository, IAdMatcher)
  Advideo.Orchestrator.Infrastructure(+ InMemory/JsonFile CampaignRepository, MapCache)
  Advideo.Orchestrator.Api           (+ endpointy /campaigns, /ads/decision, /ads/events)
  Advideo.AdServing.Matching  (NOVÝ, čistá knižnica bez I/O — scoring, pravidlá, pacing)
tests/
  Advideo.AdServing.Matching.Tests   (NOVÝ)
```

Matching je čistá knižnica (vstup: segment + kampane + kontext → výstup: rozhodnutie), takže
sa dá neskôr vytiahnuť do samostatnej služby, ak bude serving potrebovať škálovať oddelene od batch.

## 3. Dátový model

```text
Advertiser   { id, name, status }
Campaign     { id, advertiserId, name, status: draft|active|paused|ended,
               startAt, endAt, priority: 1–10, bids: [{ model: cpm|cpc|cpv|flat, amount }],
               exclusivityKey?,
               budget: { total, daily }, frequencyCap: { perViewerPerVideo, perViewerPerDay },
               targeting: Targeting, creatives: Creative[] }
Targeting    { brands[]        // entity type=brand, presná zhoda (kanonický label)
               entities[]      // {type,label} napr. product/place/org
               keywords[]      // termy z mapy (keywords + signals.text/audioKeywords)
               categories[]    // zo zdieľanej taxonómie (taxonomy.py)
               excludeCategories[], excludeKeywords[]   // brand safety / negatívny targeting
               minScore: 0–1   // minimálna istota detekcie
               videos?: { include[], exclude[] }        // voliteľne whitelist/blacklist videí
               geo?: string[] }                          // ISO kódy krajín (z requestu)
Creative     { id, url, clickUrl, width, height, altText }      // banner pre slot platformy
```

Zdieľaná taxonómia: kategórie a kanonické názvy značiek vytiahneme z `taxonomy.py` do
`schema/taxonomy.json` — číta ju Python pipeline (M1) aj .NET matcher (M2), aby sa slovník
nerozišiel. Validácia targetingu kampane proti taxonómii pri uložení.

## 3a. Platforma (konfigurácia per nasadenie)

**Platforma** = nasadenie u konkrétneho zákazníka na jeho video portál, alebo univerzálne riešenie
ako rozšírenie prehliadača (`kind: portal | browserExtension`). Všetko, čo sa líši medzi zákazníkmi,
je v `PlatformConfig`, nie v kóde:

```text
PlatformConfig {
  id, name, kind: portal|browserExtension,
  pricing:     { enabledModels: [cpm, cpc, cpv, flat], defaultCtr, defaultViewableRate,
                 flatSponsorshipEcpm, currency },
  slots:       [ { id, position: above|below|left|right, width, height } ],   // bannery okolo prehrávača
  frequency:   { minIntervalSec, maxPerVideo, maxPerViewerPerDay },
  brandSafety: { excludeCategories[], excludeKeywords[] },
  matching:    { relevanceThreshold, weights { brand, entity, keyword, category } }
}
```

- **Ceny:** CPM je primárny model. Kampaň môže mať ponuku vo viacerých modeloch (`bids[]`), použijú sa
  len modely povolené na platforme. Pre spoločné poradie sa všetko prepočíta na eCPM:
  CPC × CTR × 1000 (CTR z platformy, po 1 000 zobrazeniach skutočné CTR kampane), CPV × viewable rate × 1000,
  Flat (sponzorstvo) = pevné vysoké eCPM, aby pri zhode vyhralo.
- **Umiestnenie:** banner okolo prehrávača v slotoch platformy; vyberie sa najväčší creative, ktorý sa do
  slotu zmestí (presná veľkosť má prednosť). Kampaň bez vhodného creative na danej platforme nepostupuje.
- **Frekvencia a brand safety:** limity a vylúčenia sú na úrovni platformy (pre všetky kampane) a kampaň
  môže pridať vlastné (frequency cap, exclude kategórie/kľúčové slová).
- Kampaň môže cieliť na konkrétne platformy (`targeting.platforms`, prázdne = všetky).

## 4. Algoritmus párovania

Vstup: segment s `keywords[{term,score}]`, `entities[{type,label,score}]`, `categories[]`, `signals`.

1. **Kandidáti** — kampane `active`, v čase `startAt..endAt`, s rozpočtom a nevyčerpaným
   frequency capom, geo zhoda. Inverzný index `term/brand/category → campaignIds` (budovaný pri
   načítaní kampaní) → nemusíme prechádzať všetky kampane.
2. **Vylúčenia (hard filter)** — segment obsahuje `excludeCategories` / `excludeKeywords`
   → kampaň vypadne (brand safety má prednosť pred všetkým).
3. **Relevance score 0–1** (vážený súčet najlepších zhôd, so stropom):

   | zhoda | váha | príklad |
   |---|---|---|
   | brand entity | 1.0 | segment „Tesla“ ↔ kampaň Tesla |
   | iná entita (product/org/place) | 0.8 | „Model Y“ |
   | keyword (vizuálny/OCR/audio) | 0.6 | „electric car“ |
   | kategória | 0.4 | automotive |

   `relevance = min(1, Σ váha × scoreDetekcie)`; zhody pod `targeting.minScore` sa ignorujú.
   Výsledok obsahuje zoznam zhôd (`matchedOn`) pre vysvetliteľnosť.
4. **Ranking** — `rank = relevance × eCPM × priorityFactor × pacingFactor` (eCPM z najlepšej ponuky v povolenom modeli)
   - `priorityFactor = 0.5 + priority/10` (manuálna priorita agentúry),
   - `pacingFactor` znižuje ponuku, keď kampaň míňa denný rozpočet rýchlejšie, než má (rovnomerné rozloženie počas dňa).
5. **Prah** — ak najlepší `relevance < platform.matching.relevanceThreshold` (default 0.3) → žiadna reklama (radšej nič než nesúvisiaca).
6. **Konkurenčná exkluzivita** — v jednom videu sa neukážu dve kampane s rovnakým `exclusivityKey`
   za sebou (napr. dve automobilky), ak to inzerent vyžaduje.

Determinizmus: pri rovnosti rozhoduje `campaignId` (stabilné testy).

## 5. API (Ad decision)

```http
GET /ads/decision?videoId=abc&t=42.5&viewer=<anonymné id>&geo=SK
200 {
  "decisionId": "dcs_…",
  "segment": { "startSec": 40, "endSec": 45 },
  "ad": { "campaignId": "...", "creative": { ...Creative }, "showFromSec": 40, "showToSec": 45 },
  "matchedOn": [ { "kind": "brand", "term": "Tesla", "score": 0.92 } ],
  "trackingUrls": { "impression": "/ads/events?…&type=impression", "click": "…" }
}
204  — žiadna vhodná reklama (alebo segment chýba)
```

```http
POST /ads/events   { decisionId, type: impression|viewable|click|close, t }
```

- Udalosti aktualizujú rozpočet, frequency cap a štatistiky (M2: v pamäti / jednoduché úložisko).
- `viewer` je anonymné ID z first-party cookie snippetu (súhlas a GDPR riešime v M3), na serveri sa
  ukladá len hash.
- Admin CRUD: `GET/POST/PUT /campaigns`, `POST /campaigns/{id}:pause|resume`,
  `GET /campaigns/{id}/stats`.
- Debug: `GET /ads/explain?videoId&t` — vráti všetkých kandidátov so skóre a dôvodom vyradenia.

Výkon: mapa videa sa načíta z `IMapStore` raz a drží v cache (LRU), segment pre `t` sa hľadá
binárnym vyhľadávaním. Matching nevolá DB ani sieť počas requestu.

## 6. Úložisko (M2)

Za rozhraniami ako v M1: `ICampaignRepository`, `IEventSink`, `IBudgetLedger`.
Implementácia pre M2: in-memory + načítanie kampaní z JSON súboru (seed). Postgres a Redis
(počítadlá rozpočtu a frequency capov) neskôr — rozhrania sú na to pripravené.

## 7. Testovanie

- Unit testy matchera: brand > keyword > kategória, vylúčenia, minScore, prah, pacing,
  frequency cap, determinizmus pri rovnosti.
- Golden testy: mock mapa z M1 pipeline + seed kampane → očakávané rozhodnutia pre zoznam časov.
- API integračné testy (`WebApplicationFactory`): decision 200/204, events, explain.
- Výkonový test: 1 000 kampaní, 10 000 rozhodnutí → p95 meraný v teste (cieľ < 30 ms, reálne < 1 ms).

## 8. Rozdelenie na kroky

| krok | obsah |
|---|---|
| T-005a | `schema/taxonomy.json` + úprava pipeline (`taxonomy.py` číta JSON), modely kampaní, matcher knižnica + unit testy |
| T-005b | API: campaigns CRUD (in-memory + seed JSON), `/ads/decision`, `/ads/explain`, map cache |
| T-005c | `/ads/events`, rozpočet, pacing, frequency cap, štatistiky kampane + golden a výkonové testy |

Každý krok = samostatný commit, testy zelené, záznam do memory.

## 9. Rozhodnutia (Braňo, 2026-11-11)

1. **Cena** — CPM prioritne; ďalšie modely (CPC, CPV, flat) konfigurovateľné per platforma.
2. **Formát** — banner okolo prehrávača, umiestnenie (sloty) konfigurovateľné per platforma.
3. **Frekvencia** — konfigurovateľná per platforma (default: min. 60 s medzi reklamami, max 3 za video).
4. **Brand safety** — konfigurovateľná per platforma (default: bez vylúčení), kampaň môže pridať vlastné.

## 10. Stav implementácie

- **T-005a (hotové):** `schema/taxonomy.json` (pipeline aj matcher čítajú ten istý súbor),
  knižnica `Advideo.AdServing.Matching` (modely kampaní, platforiem, content mapy; `AdMatcher`, `Pricing`,
  `Taxonomy` s validáciou kampaní), 31 unit testov.
- **T-005b (hotové):** API platformy a kampane (CRUD, validácia, pause/resume, seed JSON),
  `GET /ads/decision?platform&videoId&t` (200 reklama / 204 + `X-Advideo-NoAd-Reason`, 404 neznáma platforma),
  `GET /ads/explain`, LRU+TTL cache máp nad `IMapStore` (zdieľané načítanie, invalidácia po novej analýze),
  počítadlá diváka zatiaľ posiela snippet (`lastAdAt`, `adsInVideo`, `prevCampaign`); 21 testov orchestrátora.
- **T-005c (hotové):** `POST /ads/events` (impression/viewable/click/close, deduplikácia per rozhodnutie),
  ledger: čerpanie podľa modelu (CPM za zobrazenie, CPV za viditeľné, CPC za klik, flat 0), štatistiky
  `GET /campaigns/{id}/stats`, počítadlá diváka cez HMAC hash `viewer` id (frekvencia v platforme aj v kampani,
  posledná reklama, exkluzivita), pacing a pozorované CTR z reálnych čísel; min. interval sa meria od aktuálneho
  času prehrávania `t`. Golden test (mapa z M1 pipeline + seed) a výkonový test: 1 000 kampaní, 10 000 rozhodnutí,
  p50 ≈ 1,8 ms, p95 ≈ 3,5 ms (bez inverzného indexu). Spolu 71 .NET testov.

### Ďalej (mimo M2)
- autentifikácia admin API (API kľúč per platforma), Redis/Postgres pre ledger a repozitáre,
- inverzný index kampaní (pri desaťtisícoch kampaní), M3 JS snippet.
