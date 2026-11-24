# Analyzer eval (T-009)

Measures how well the real backends (YOLO+CLIP, OCR, Whisper) describe real videos, against manually
annotated ground truth, and how fast they are.

```bash
pipeline/eval/run-mac.sh                     # venv + [ml] deps, download clips, run both configs (MPS on Apple Silicon)
# or step by step
python eval/fetch.py                         # 8 clips from Wikimedia Commons -> eval/videos/*.mp4 (720p, gitignored)
python eval/run_eval.py --config yolo+clip --config "yolo+clip,ocr+whisper"
python eval/run_eval.py --config yolo+clip --opt yolo+clip.clip_threshold=0.4 --min-support 1   # experiments
```

- `samples.json`: clips (Commons title, licence) + ground truth per time range:
  `categories` expected (precision + recall), `allowed` neutral, `terms` expected keywords, `brands`.
- Scored per 6 s window (same as the content map). `--cache` reuses per-video maps so long CPU runs can
  continue in steps. Results: `eval/results/<config>.json` (per video, per segment keywords).
- `ADVIDEO_DEVICE=cpu|mps|cuda` overrides device selection.

## Results 2026-11-12 (sandbox, 4-core arm64 CPU, fps 1, 228 s of video in 8 clips)

| step | cat P | cat R | term R | speed |
|---|---|---|---|---|
| mock analyzer (baseline) | 0.22 | 0.10 | 0.12 | – |
| yolo+clip as it was | 0.90 | 0.18 | 0.43 | 0.19× RT |
| + CLIP logit scale fix, all categories per term, taxonomy (COCO food, cooking, running→fitness) | 0.86 | 0.78 | 1.00 | 0.18× |
| + neutral CLIP distractor labels | 0.95 | 0.80 | 1.00 | 0.18× |
| + softmax per label kind (action/scene/object), `sneakers` label | 0.87 | 0.94 | 0.98 | 0.20× |
| + temporal support (≥2 frames or score ≥0.8), clip_threshold 0.5 → **new defaults** | **1.00** | **0.92** | 0.94 | 0.18× |
| yolo+clip + OCR (every 2 s) + Whisper base | 1.00 | 0.92 | 0.94 | 1.19× |

RT = processing time / video duration (0.18× = 1 minute of video in ~11 s on CPU).

Findings
- The CLIP backend was effectively off: softmax over raw cosine similarities (no `logit_scale`) is near uniform,
  no label ever passed the threshold. Fixed; CLIP now drives actions/scenes (driving, running, cooking).
- Closed-set zero-shot always picks *something* → neutral distractors (crowd, screen, close-up, …) and a separate
  softmax per kind are needed; single-frame noise is removed by the temporal support filter.
- Remaining misses: windows where the subject is out of frame (running-form 24–30 s, 2 s tail).
- OCR works on captions/overlays (recipe text, ad copy on a bus) but gives no category gain on this set and is
  the slowest backend (now every 2 s, downscaled to 960 px). Stylised logos (Coca-Cola script) are not text → need
  logo detection. Whisper: picks spoken keywords ("oven", "sandwich"); videos without audio no longer crash.
- Bugs fixed on the way: Whisper crashed on videos without an audio track (failed the whole job); the pipeline
  closed analyzer instances owned by the caller (breaks a worker that keeps models loaded).

## R1 brands / logos (2026-11-16)

7 brand clips added (`set: brands` in samples.json: VW T2, Audi, BMW film, Toyota without visible logo,
Nike, Pepsi Max, Tesla factory) → 15 clips, 493 s, 29 brand windows. Ground truth may use `brandsAllowed`
(brand identifiable from the product but no legible logo: not rewarded, not penalised).

New opt-in frame analyzer **`clip-brand`**: CLIP zero-shot over logo prompts (`brands[].clipPrompts` in
taxonomy.json) + neutral distractors (`clipBrandNeutral`, incl. generic products: car, running shoe, soda can…)
on full frame + 2×2 tiles + centre crop; detections kind `logo` → `signals.logos` + brand entity. Logos use a
stricter temporal filter (≥3 frames per window or one ≥0.97). `dump_brand_scores.py` dumps raw per-frame
scores for offline threshold tuning.

| config (15 clips, CPU) | brand R | false brands | cat P / R | speed |
|---|---|---|---|---|
| yolo+clip,ocr (brands only from text) | 0.28 | 0 | 0.81 / 0.93 | 0.93× RT |
| **yolo+clip,ocr,clip-brand** | **0.52** | 2 | 0.81 / 0.93 | 1.07× RT |

Findings
- OCR reads brands written as text (BMW disclaimer, NIKE on a shirt, PEPSI MAX title) but no logos.
- CLIP finds logos it knows well (Nike swoosh 9/10 windows, Audi rings, TESLA lettering), misses small ones
  (VW badge on a moving van, BMW roundel) — tiles help but logos < ~5 % of the frame stay invisible.
- CLIP conflates a product category with its dominant brand (running shoe → Nike, electric car → Tesla):
  generic product distractors + logo-only prompts removed this (running-form false Nike 19 frames → 0).
- Remaining false brands: Red Bull on a Pepsi shelter frame, Audi on a Tesla assembly line.
- Next for higher recall/precision: a dedicated logo detector (YOLO trained on a logo dataset, e.g.
  LogoDet-3K / OpenLogo, restricted to customer brands), crops from YOLO vehicle/person boxes.

Gaps: 7 brand clips only, mostly one brand each; brand recall numbers have wide error bars.

Clips: Wikimedia Commons, licences in `samples.json` (CC0 / CC BY / CC BY-SA); used for evaluation only, not redistributed.

## R1c + R3: bigger eval set, label fixes, regression guard (2026-11-16)

11 more clips (`set: r3`): beach football, tennis, outdoor gym, beach, airplane takeoff, traditional dance,
supermarket, mountains, Tour de France, skateboarding, political street demonstration (negative, brand-safety
relevant) → **26 clips, 823 s**.

| yolo+clip, 26 clips | cat P | cat R | term R |
|---|---|---|---|
| before (T-009 taxonomy) | 0.79 | 0.75 | 0.93 |
| + neutral talking-head actions (R1c: interview, talking to camera, sitting, posing, looking at a screen) | — | — | — |
| + sport actions (football, tennis, skateboarding, cycling, basketball, working out), stadium / sports field, supermarket + food/drink shelves, "a landscape" → outdoor (was a neutral that stole mountain/outdoor), no "swimming" | **0.89** | **0.96** | 0.94 |

On the 15-clip set R1c alone moved category precision 0.81 → 0.88 (Nike interviews no longer "running" → fitness).
Brand config `ocr,clip-brand` on 26 clips: brand R 0.52, 3 false brands (new: green four-square regional logo →
Microsoft).

Regression guard: `baseline.json` + `check_regression.py` (exit 1 when cat P/R or term R drop > 0.03, brand R
drops > 0.05 or false brands increase). Run after any taxonomy/threshold change:

```bash
python eval/run_eval.py --config yolo+clip --config "ocr,clip-brand" --cache
python eval/check_regression.py            # --update to accept a deliberate improvement
```

Remaining errors: YOLO tv/laptop/cell phone → tech/electronics on real screens (shop TV, bus shelter screen);
running-form windows without the runner; beach football / cycling single windows.

## R9 + R8: screen classes, brand safety (2026-11-16)

**R9** — YOLO per-class minimum confidence for screens (`class_conf`, default tv/laptop/cell phone ≥ 0.6):
26 clips cat P 0.89 → 0.93, recall unchanged.

**R8 brand safety** — new CLIP label kind `safety` (own softmax): protest/demonstration, riot police, sit-in,
political rally (→ `sensitive`, `politics`), car crash, emergency vehicles at an accident, burning building,
wildfire (→ `accident`), soldiers with rifles, firing a gun (→ `weapons`), fighting, blood/injury
(→ `violence`), against ~22 everyday neutrals (sports crowd, concert, festival, contest spectators, fire station,
parked fire truck, barbecue, normal traffic, …). `safety_threshold` 0.7 and always ≥ 2 frames per window
(no single-frame bypass). Signal `signals.safety`. 6 clips added (`set: safety`: Kolkata protest, riot police,
car accident, building fire, live-fire exercise, hard negative firefighters' games) → **32 clips, 1031 s**.

| yolo+clip, 32 clips | cat P | cat R | sensitive P | sensitive R |
|---|---|---|---|---|
| first label set | 0.72 | 0.90 | 0.65 | 0.86 |
| + event-crowd neutrals, sit-in + emergency-vehicle labels, threshold 0.7 / 2 frames | **0.85** | **0.93** | **0.94** | **0.92** |

Tuned offline from `dump_safety_scores.py` (per-frame softmax of the safety group). Remaining: 2 false blocks
(Coca-Cola pavilion crowd), the Kolkata protest (crowd under a tent, no banners) mostly missed.

Ad server: segments with category `sensitive` get **no ad** unless `platform.brandSafety.allowSensitive`
(admin UI checkbox); subtypes can be excluded individually via `excludeCategories`.
`baseline.json` now covers 32 clips incl. `perCategory.sensitive.*`.

**R8b brand safety from speech / on-screen text** — lexicon in `schema/taxonomy.json` (`safetyTerms`: 14 groups,
EN/SK/CS/DE, diacritics-insensitive, `*` prefix for SK/CS inflection, idiom `exclusions` such as "zabili čas",
"crash course", "fire station"; `newsMarkers`). Whisper and OCR emit `safety_speech` / `safety_ocr` detections
(`signals.safety`: "speech: killed", "on-screen: …") and `news` cues. Per 6 s window: one **high** hit or
**≥ 2 different medium** hits are needed (a lone "attack" or "war" does not block ads); profanity/drugs are
categories only (blocked by the `strict` preset), and they never corroborate a sensitive hit.

| set | cases | precision | recall | note |
|---|---|---|---|---|
| `text_safety_cases.json` (tuning) | 83 | 1.00 | 1.00 | written with the lexicon — optimistic |
| `text_safety_holdout.json` v1 (before fixes) | 20 | 0.67 | 0.40 | then moved into the tuning set |
| `text_safety_holdout.json` v2 (never tuned) | 20 | **1.00** | **0.55** | realistic estimate |
| 32 eval videos, OCR + Whisper (181 windows) | — | 1 false block → fixed ("on-screen: fire" + "speech: army") | — | eval clips have little speech |

Run `python eval/run_text_safety_eval.py` (tuning set). The lexicon is precise but misses paraphrases
("rescue teams searching the rubble", "boje na hraniciach", "zaútočil nožom") — next step: a multilingual
sentence-embedding classifier on top of the lexicon (R8c, below).

**R8c semantic text safety** — `semantic_safety.py`: Whisper segments are embedded with
`paraphrase-multilingual-MiniLM-L12-v2` (transformers, mean pooling) and compared with 54 sensitive prototypes
(violence, death, war, terror, disaster, accident; EN+SK) and 45 neutral ones (`schema/taxonomy.json` →
`semanticSafety`). score = mean top-3 cosine to sensitive − mean top-3 cosine to neutral; ≥ 0.21 → high hit,
≥ 0.14 → medium (needs one more hit). Thresholds = highest SAFE score on the tuning set (0.087) + 0.12 / + 0.05,
chosen by `eval/semantic_safety_eval.py --search` on the tuning set only. Cost: model load ~3 s, ~3.4 ms per
segment on CPU (~470 MB download on first use; `ADVIDEO_SEMANTIC_SAFETY=0` disables it).

| set (never tuned) | lexicon P / R | lexicon + semantic P / R |
|---|---|---|
| held-out v2 (20) | 1.00 / 0.55 | **1.00 / 0.91** |
| held-out v3 (24, written before the first semantic run) | 1.00 / 0.08 | **1.00 / 0.67** |
| real speech of the 32 eval videos (139 Whisper segments) | no hit | no hit (max score 0.104 < 0.14) |

Still missed: implicit events without violent words ("militants blew up a bridge", "body found in the woods",
"avalanche buried skiers"). The eval videos contain little speech — real news/vlog transcripts are the next
eval need before raising the thresholds' trust.

## R8d: real broadcasts (2026-11-24)

Whole Slovak news programmes instead of single sentences: **STVR Správy 23.11.2026** (50 min, main news) and
**TV JOJ Krimi 23.11.2026** (23 min, crime news) as the dev set, **TV JOJ Krimi 22.11.2026** as a holdout that was
labelled before any run and never tuned on. Audio → faster-whisper (`small` and `base`, language `sk`); stories
labelled by Claude from the transcript — `S` sensitive / `N` safe for ads / `A` ambiguous (not scored); human review
recommended. Only the labels are committed (`eval/broadcast/*.labels.json`, `eval/broadcast-holdout/`); media and
transcripts are copyrighted and stay local. Run:
`python eval/broadcast_safety_eval.py --transcripts DIR [--labels eval/broadcast-holdout] [--context 1]`.

Metrics: *window recall* = share of 6 s windows inside sensitive stories that are flagged; *story recall* = sensitive
stories with ≥ 1 flagged window; *false-block rate* = flagged windows inside safe stories (lost inventory).

| set | rules | window recall | story recall | false-block rate | false stories |
|---|---|---|---|---|---|
| dev (2 broadcasts, 13 S / 29 N stories) | R8c lexicon | 0.09 | 7/13 | 0.002 | 1/29 |
| | R8c lexicon + semantic | 0.39 | 12/13 | **0.062** | **12/29** |
| | **R8d lexicon** | 0.27 | 10/13 | 0.004 | 2/29 |
| | **R8d lexicon + semantic** | 0.48 | 12/13 | **0.024** | 6/29 |
| holdout (3 S / 7 N stories) | R8c lexicon | 0.06 | 2/3 | 0 | 0/7 |
| | R8c lexicon + semantic | 0.33 | 3/3 | 0.055 | 5/7 |
| | **R8d lexicon** | 0.17 | **3/3** | 0 | 0/7 |
| | **R8d lexicon + semantic** (`en,sk,cs,de`) | 0.33 | 3/3 | 0.046 | 3/7 |
| | **R8d lexicon + semantic** (`sk` only) | 0.32 | 3/3 | **0.028** | **1/7** |

What changed (tuned on dev only):
1. **Context corroboration** — medium hits may be confirmed by hits in the neighbouring window on each side (18 s):
   news spreads its cues over sentences ("zranený" … "smrť").
2. **Semantic hits never corroborate each other** and are high only from 0.32 (was 0.21): the MiniLM classifier
   repeats on routine police/court talk ("Mestská polícia sa prípadom ďalej zaoberá" scored 0.36). Below 0.32 a
   semantic hit needs a lexicon word within ±1 window. Sentence-level held-out v2 stays at recall 0.91; v3 drops
   0.67 → 0.33 (isolated paraphrases without any keyword) — the price for 2.5× fewer false blocks on real news.
3. **Slovak/Czech lexicon gaps** from real speech: inflected *znásilnenie*, *masaker/masakra*, *postrelil*,
   *prestrelka*, *dobodal*, *sexuálne vykorisťovanie/zneužívanie*, *bomba/bombová hrozba*, *drony*, *balistické
   strely*, *smrteľne*, *obesil sa*, *troskách*.
4. **Language packs** — matching only the broadcast's language removes cross-language false hits (*rifle* = jeans).

Whisper model matters more than any threshold: with `base` the dev lexicon story recall is 6/13 (vs 10/13 with
`small`), lexicon + semantic 11/13; Slovak transcripts from `base` are barely readable. Remaining errors: series titles in programme
trailers ("Vraždy v Dolinách"), Whisper mis-hearings ("Veľká dopravná akcia" → "Válka"), routine police stories
(truck checks, drunk man rolling a stone ball) flagged by the semantic classifier, story-boundary windows.
Next: a stronger classifier (multilingual-e5 / small LLM judge on 30 s story chunks) and more labelled broadcasts.

## R8e: stronger classifier — story chunks (2026-11-24)

Two subagents, kept apart: **R8e-B** benchmarked classifiers on the R8d dev set only (`eval/r8e_bench/`);
**R8e-A** built **holdout2** (`eval/broadcast-holdout2/`, labelled before any model run): STVR *Góly-body-sekundy*
(SK sport, 0 S / 14 N — pure false-block test), M1 *Híradó* (HU, 2/12/4), DW English reel (18 videos, 3/10/5),
DW Polska reel (30 videos, 3/20/7). TVP was geo-blocked, no full public EN bulletin was found → DW media-centre reels.

Dev benchmark (lexicon + classifier, production window rule): MiniLM per segment (R8d) FBR 0.024 · **MiniLM on 24 s
chunks (stride 8 s), thresholds 0.32/0.18: FBR 0.010, story recall 12/13** · e5-small/base + LR 0.006–0.014 (grid-tuned
on dev) · mDeBERTa NLI 0.020 (1.1 s/chunk) · Qwen2.5-0.5B judge 0.014 but recall 11/13. Winner = chunking, same model
(23 ms/chunk ≈ 3 ms per second of audio). Config: `semanticSafety.unit = "chunk"` (`"segment"` = R8c/R8d behaviour).

Holdout evaluation, run once, languages pre-registered per programme (`sk,en` · `hu,en` · `en` · `pl,en`):

| programme | lexicon FBR / story recall | R8d lex+sem FBR / recall | **R8e lex+sem FBR / recall** |
|---|---|---|---|
| JOJ Krimi 22.11 (old holdout, SK) | 0.000 · 3/3 | 0.046 · 3/3 | **0.000 · 3/3** |
| STVR sport (SK) | 0.018 · – | 0.042 · – | **0.018** · – |
| M1 Híradó (HU) | 0.008 · 1/2 | 0.008 · 1/2 | 0.008 · 1/2 |
| DW English | 0.000 · 3/3 | 0.000 · 3/3 | 0.000 · 3/3 |
| DW Polska | 0.016 · 2/3 | 0.063 · 3/3 | 0.063 · 2/3 |

Slovak and English: semantic false blocks are gone (the classifier no longer adds any on SK). Remaining errors are
lexicon / labelling issues, now known — fixing them requires a fresh holdout3, holdout2 is no longer "unseen":
- sport vocabulary: *streľba/strela* (basketball shot), *zranený* hráč → needs sport-context exclusions;
- HU *terrorizálhat* (metaphorical), PL: military-cooperation / Ukraine-summit politics labelled N but scored as war
  (label policy question: is war *politics* without violence sensitive? — decide per preset, e.g. News vs Strict);
- missed: bomb threats at schools (HU), school violence report (PL) — implicit wording; HU/PL lexicons are still
  unreviewed by native speakers.

