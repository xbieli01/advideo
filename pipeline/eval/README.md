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

Gaps of this eval set: no clip with clear brand text/logo, little speech, 8 clips only. Add brand-rich and
customer-like videos before trusting brand recall.

Clips: Wikimedia Commons, licences in `samples.json` (CC0 / CC BY / CC BY-SA); used for evaluation only, not redistributed.
