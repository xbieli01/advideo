# advideo

Contextual in-video advertising — "Google Ads for what is on screen right now".

A streaming platform embeds our JS snippet. Offline, every video is analyzed
into a **JSON content map** (timestamped keywords / entities / categories). At
playback the snippet reads the map for the current moment and asks the
ad-serving API which advertiser matches — a Tesla product shot shows a Tesla ad
(if Tesla is a customer), a car scene otherwise matches VW, a running scene
matches Nike, and so on.

## Milestones

- **M1 (done):** offline pipeline `video → JSON content map` + .NET orchestrator.
- **M2 (done):** ad-serving API + keyword↔campaign matching engine, configurable per platform — design `docs/m2-ad-serving.md`; matcher (T-005a), ad-serving API + map cache (T-005b), events/ledger/stats + golden & perf tests (T-005c).
- **M3 (in progress):** JS snippet for customer portals done (M3a, `client/`); browser extension next (M3b).
- **Brand safety (done):** sensitive content is detected from the picture (protests, accidents, weapons, violence) and from speech / on-screen text in EN/SK/CS/DE (violence, death, war, terror, disasters, accidents, weapons; profanity and drugs as separate categories; news-report cues). Platforms choose a preset: `standard` (no ads in sensitive content), `strict` (+ profanity, drugs) or `news` (sensitive allowed in news reports).
- **Robust processing (done, R5):** videos from URLs and HLS, retries with backoff, dead-letter, restart recovery and bulk catalogue import (see below).
- **Admin UI (done):** Angular SPA for campaigns, platforms, video analysis and decision explain, served by the API under `/admin` (`admin/`).

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
schema/taxonomy.json             shared categories / brands (pipeline + ad matcher)
docs/m2-ad-serving.md            M2 design (platforms, pricing, matching, API)
client/                          player snippet (TypeScript, M3)
admin/                           Admin UI (Angular 22 + Material), served by the API at /admin
pipeline/                        Python analysis worker (M1)
  src/advideo_pipeline/          package (frames, analyzers, aggregate, pipeline, cli)
  tests/                         end-to-end + schema-validation tests
orchestrator/                    .NET orchestration + ad serving
  src/Advideo.AdServing.Matching/ pure ad decision engine (no I/O)
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

## Robust processing (R5)

**Video sources.** `videoUri` may be a local path, `file://`, an `http(s)://` video file or an HLS
playlist (`…/index.m3u8`). The pipeline materializes remote sources once into a temp file
(`pipeline/src/advideo_pipeline/sources.py`): streamed download with a size limit and retries
(honours `Retry-After`), HLS remuxed by ffmpeg without re-encoding, HTML error pages rejected.
For S3 use presigned https URLs. SSRF guard: loopback/private addresses are blocked unless
`ADVIDEO_ALLOW_PRIVATE_HOSTS=1`; optionally restrict hosts with `ADVIDEO_ALLOWED_HOSTS=cdn.example.com,*.example.net`.
Other knobs: `ADVIDEO_MAX_DOWNLOAD_BYTES` (5 GiB), `ADVIDEO_DOWNLOAD_TIMEOUT` (60 s), `ADVIDEO_HLS_TIMEOUT`
(3600 s), `ADVIDEO_DOWNLOAD_RETRIES` (3).

**Exit codes of `advideo-analyze`:** `0` ok · `3` permanent source error (404/403, not a video, too large,
blocked host) · `4` transient source error (network, 5xx, timeout) · `1` unexpected error.

**Retries and dead letter.** The worker retries transient failures (exit ≠ 3, timeouts, crashes) with
exponential backoff — `Worker:MaxAttempts` (3), `Worker:RetryBaseDelaySeconds` (30, doubles),
`Worker:RetryMaxDelaySeconds` (900). Between attempts a job is `Retrying` (`attempts`, `nextAttemptAt`).
Permanent errors and exhausted retries end in `Failed` — the dead-letter state:
`GET /videos?status=Failed` lists them, `POST /videos/{id}/retry` re-queues one (attempts reset).
On start-up the worker re-queues `Queued`, interrupted `Running` and `Retrying` jobs from the
repository (with `Persistence:Provider=postgres` nothing is lost on restart).

**Bulk catalogue import.** `POST /videos/bulk` (admin key), up to 1000 videos per request:

```json
{ "analyzer": "yolo+clip,ocr", "track": "whisper", "force": false,
  "items": [ { "videoId": "movie-001", "videoUri": "https://cdn.example.com/movie-001.mp4" },
             { "videoId": "show-17",   "videoUri": "https://cdn.example.com/show-17/index.m3u8", "analyzer": "yolo+clip" } ] }
```

The response lists `queuedIds`, `skipped` (already queued/running, or already analyzed from the same URI —
re-imports are idempotent unless `force`) and `invalid` items (missing/unsafe id, unsupported scheme,
duplicates). Video ids may contain only letters, digits, `.`, `_`, `-` (max 128). The Admin UI
(*Videos → Import catalogue*) accepts the same as CSV (`videoId,videoUri[,analyzer[,track]]`) or JSON,
filters by status and offers *Retry* on failed jobs.

## Admin UI

Angular 22 + Angular Material SPA in `admin/`. In the Docker image it is built in a
non-root Node stage and copied to `wwwroot/admin`; the API serves it at **`/admin`**
on the same origin (no CORS), with a strict CSP (`script-src 'self'`), `X-Frame-Options: DENY`,
`no-store` for `index.html` and immutable caching for hashed bundles.

- Sign-in is the API key (`X-Api-Key`), validated via `GET /auth/me`; kept in sessionStorage only.
- `admin` keys: full CRUD on campaigns/platforms, pause/resume, video analysis jobs, decision explain.
- `reporting` keys: read-only views (Overview, Campaigns, Platforms) with stats.

Local development (API on :5000 with the Development seed keys `dev-admin-key` / `dev-reporting-key`):

```bash
cd orchestrator/src/Advideo.Orchestrator.Api && ASPNETCORE_ENVIRONMENT=Development \
  ASPNETCORE_URLS=http://localhost:5000 dotnet run --no-launch-profile
cd admin && npm ci && npx ng serve        # http://localhost:4200/admin/  (proxy.conf.json -> :5000)
npx ng test --watch=false                 # Vitest unit tests
```

Preview the production build through the API: `npx ng build` and copy `admin/dist/admin/` to
`orchestrator/src/Advideo.Orchestrator.Api/wwwroot/admin/` (gitignored); then open `http://localhost:5000/admin/`.

## Status

M1: schema, mock pipeline, CLI, tests, and real backends behind the `[ml]`
extra — **YOLO+CLIP** (objects/scenes), **OCR** (on-screen brands), **Whisper**
(spoken brands/keywords). Next: the .NET orchestrator + queue + object store.
