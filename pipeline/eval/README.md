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
