# advideo

Contextual in-video advertising — "Google Ads for what is on screen right now".

A streaming platform embeds our JS snippet. Offline, every video is analyzed
into a **JSON content map** (timestamped keywords / entities / categories). At
playback the snippet reads the map for the current moment and asks the
ad-serving API which advertiser matches — a Tesla product shot shows a Tesla ad
(if Tesla is a customer), a car scene otherwise matches VW, a running scene
matches Nike, and so on.

## Milestones

- **M1 (this repo, in progress):** offline pipeline `video → JSON content map`.
- M2: ad-serving API + keyword↔campaign matching engine.
- M3: JS client SDK / snippet.

## Architecture (short)

Hybrid stack. **.NET** owns orchestration + the ad-serving API (typed, fast,
great concurrency). **Python GPU workers** own video analysis (PyTorch / YOLO /
CLIP / OCR / Whisper ecosystem). Throughput is GPU-bound and workers scale
horizontally, so the orchestration language doesn't limit it. The two sides talk
only through the **JSON content map** (`schema/video-map.schema.json`) and a
queue job message, so each evolves independently.

```
video → sample frames (ffmpeg) → analyzers (models) → aggregate → JSON map → object store
                                                                                   ↑
browser snippet reads map at time t → ad-serving API matches keywords↔campaigns → ad
```

## Repo layout

```
schema/video-map.schema.json     canonical content-map contract
pipeline/                        Python analysis worker (M1)
  src/advideo_pipeline/          package (frames, analyzers, aggregate, pipeline, cli)
  tests/                         end-to-end + schema-validation tests
orchestrator/                    .NET orchestration (stub in M1)
```

## Quickstart (pipeline, no GPU needed)

```bash
cd pipeline
pip install -e .[dev]
advideo-analyze --video any.mp4 --duration-sec 12 --out map.json
pytest
```

The default analyzer is a deterministic **mock**, so the whole pipeline runs and
is testable with no models. Real backends implement the `Analyzer` interface in
`pipeline/src/advideo_pipeline/analyzers/` and are selected via `--analyzer`.

### Real analyzer (YOLO + CLIP)

```bash
cd pipeline
pip install -e .[ml]          # torch, ultralytics (YOLOv8), open-clip, opencv
advideo-analyze --video real.mp4 --analyzer yolo+clip --fps 1 --out map.json
```

- **YOLOv8** detects on-screen objects (COCO classes) → keywords/objects.
- **CLIP** scores each frame zero-shot against a scene/action label set
  (`taxonomy.CLIP_SCENE_LABELS`) → actions/scenes, no training needed.
- Both map through `taxonomy.py` so categories match the mock's vocabulary.
- Auto-uses CUDA when available (throughput comes from the GPU + horizontal
  worker scaling), falls back to CPU. Heavy deps are imported lazily, so the
  mock path and tests never load torch.

### Brand (OCR) and audio (Whisper) backends

```bash
# on-screen text -> brand entities + audio track -> spoken brands/keywords
advideo-analyze --video real.mp4 --analyzer "yolo+clip,ocr" --track whisper --out map.json
```

- **OCR** (`ocr`, frame-level, EasyOCR): reads on-screen text; a "TESLA"
  wordmark becomes a brand entity — the exact-brand-match use case.
- **Whisper** (`whisper`, track-level, faster-whisper): transcribes the audio
  once and maps spoken brands/keywords onto the segments they fall in.
- Two analyzer seams: **frame-level** (`Analyzer`, per sampled frame — mock,
  yolo+clip, ocr) and **track-level** (`TrackAnalyzer`, whole video — whisper).
  Brand/keyword extraction from recognized/transcribed text lives in the pure,
  model-free `textmatch.py`, so that logic is fully unit-tested.

## Status

M1: schema, mock pipeline, CLI, tests, and real backends behind the `[ml]`
extra — **YOLO+CLIP** (objects/scenes), **OCR** (on-screen brands), **Whisper**
(spoken brands/keywords). Next: the .NET orchestrator + queue + object store.
