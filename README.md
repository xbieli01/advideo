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

## Status

M1 scaffold: schema, runnable mock pipeline, CLI, tests. Next: plug a real
analyzer (YOLO objects + CLIP zero-shot labels), then wire the .NET orchestrator
and queue.
