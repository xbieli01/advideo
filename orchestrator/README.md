# advideo orchestrator (.NET)

Batch service that turns the platform's video list into stored JSON content maps.
It owns ingest, the job queue, running the Python analysis pipeline (the .NET →
Python boundary from ADR-001), and the object store the client SDK later reads
maps from.

```
POST /videos ──▶ IJobRepository (status)     ┌── AnalysisWorker (BackgroundService)
             └─▶ IJobQueue  ─────────────────┤   dequeue → IPipelineRunner (advideo-analyze)
                                             │            → IMapStore.Save
GET /videos/{id}       status                └── bounded concurrency (Worker:MaxConcurrency)
GET /videos/{id}/map   the content map (from IMapStore)
```

## Projects

```
src/Advideo.Orchestrator.Core            models + abstractions (IJobQueue, IMapStore, IJobRepository, IPipelineRunner)
src/Advideo.Orchestrator.Infrastructure  ChannelJobQueue, InMemoryJobRepository,
                                         FileSystemMapStore, S3MapStore (AWS/MinIO), CliPipelineRunner
src/Advideo.Orchestrator.Api             minimal-API host + AnalysisWorker hosted service
tests/Advideo.Orchestrator.Tests         xUnit: infra unit tests + end-to-end API test (fake runner)
```

Everything is behind interfaces: the in-memory Channel queue and filesystem
store are the local-dev defaults; swap in RabbitMQ/Service Bus and S3/MinIO in
production without touching the API or worker.

## Run locally

```bash
# 1) make the pipeline CLI available on PATH (from ../pipeline)
pip install -e ../pipeline            # provides `advideo-analyze`

# 2) run the orchestrator
dotnet run --project src/Advideo.Orchestrator.Api      # http://localhost:5xxx

# 3) enqueue a video and read its map
curl -X POST localhost:5000/videos -H 'content-type: application/json' \
  -d '{"videoId":"v1","videoUri":"/abs/path/v1.mp4","analyzer":"yolo+clip","track":"whisper"}'
curl localhost:5000/videos/v1            # status
curl localhost:5000/videos/v1/map        # the JSON content map
```

For the mock pipeline (no models) use `"analyzer":"mock"`.

## Ad serving (M2)

Design: `../docs/m2-ad-serving.md`. Platforms (customer portal or browser extension) and campaigns are
seeded from `seed/adserving.sample.json` (`AdServing:SeedFile`) into in-memory stores.

| Endpoint | Purpose |
|---|---|
| `GET/POST /platforms`, `GET/PUT/DELETE /platforms/{id}` | platform config: pricing models, banner slots, frequency, brand safety, matching |
| `GET/POST /campaigns`, `GET/PUT/DELETE /campaigns/{id}`, `POST /campaigns/{id}/pause\|resume` | campaigns (validated against `schema/taxonomy.json`) |
| `GET /ads/decision?platform&videoId&t[&viewer&geo&lastAdAt&adsInVideo&prevCampaign]` | ad for the current segment — `200` ad + `decisionId`, `204` none (`X-Advideo-NoAd-Reason`) |
| `POST /ads/events` `{decisionId, type: impression\|viewable\|click\|close, t}` | delivery events → spend (CPM per impression, CPV per viewable, CPC per click), stats, viewer frequency |
| `GET /campaigns/{id}/stats` | impressions, viewables, clicks, CTR, spend total / today |
| `GET /ads/explain?…` | all candidates with relevance, eCPM, rank and rejection reason |

`viewer` is an anonymous first-party id from the snippet; it is HMAC-hashed with `AdServing:ViewerIdSecret`
(set it per deployment) and only the hash is kept. Server counters (budget, caps, frequency, last ad) are
combined with optional client counters — the stricter value wins.
`/ads/*` sends CORS headers for `AdServing:Cors:AllowedOrigins` (Development: `*`); `/ads/events` also accepts
`text/plain` JSON bodies so `navigator.sendBeacon` works cross-origin without a preflight. Ledger and decision log are in-memory
(single instance); production target is Redis (counters) + Postgres (billing).

Content maps are cached (LRU + TTL, `AdServing:MapCache`) and invalidated when the worker stores a new map.
### Persistence & scaling

| Setting | Values | Notes |
|---|---|---|
| `Persistence:Provider` | `memory` (default) \| `postgres` | platforms, campaigns, jobs as JSONB documents (`advideo_*` tables, created at startup); `ConnectionStrings:Postgres` |
| `Delivery:Provider` | `memory` (default) \| `redis` | delivery ledger + decision log shared across API instances; money in integer micro-units; `ConnectionStrings:Redis`, `Delivery:Redis:*` TTLs |

The seed file only inserts missing ids, so changes made through the API survive restarts with Postgres.
Run more than one API instance only with `Delivery:Provider=redis` (in-memory counters are per process).

Tests: the ledger contract runs against in-memory and over the Redis protocol (in-process Microsoft Garnet,
or a real Redis via `ADVIDEO_TEST_REDIS=host:port`); Postgres repository tests run when
`ADVIDEO_TEST_PG` holds a connection string.

### Security

| Area | Access |
|---|---|
| `/healthz`, `/readyz` | public (readiness checks map store and, when enabled, Postgres / Redis) |
| `/ads/decision`, `/ads/events` | public (called from browsers), CORS-restricted, rate-limited per client IP (`RateLimit:AdsPerMinutePerClient`, 429) |
| `GET /platforms*`, `GET /campaigns*`, `/campaigns/{id}/stats` | API key with role `reporting` or `admin` |
| writes on platforms / campaigns, pause/resume, `/videos*`, `/ads/explain` | API key with role `admin` |

API keys go in header `X-Api-Key`; only their SHA-256 hashes are configured (`Security:ApiKeys[]: {name, sha256, roles}`),
generate with `openssl rand -hex 32` and hash with `printf %s "$KEY" | sha256sum`. Development appsettings contain a
local-only key `dev-admin-key`. Behind a reverse proxy set `Network:TrustForwardedHeaders=true` so rate limiting
sees the real client IP.

## Object store

Default is the filesystem (`Storage:Provider=filesystem`, maps under
`./data/maps`). For S3/MinIO set `Storage:Provider=s3` and run
`docker compose up -d` (MinIO on :9000, console :9001, bucket auto-created).
Credentials come from the standard AWS chain (env/profile/role) — never in code.

## Test

```bash
dotnet test      # 7 tests: infra units + POST→worker→store→GET end-to-end
```

## Notes

- OpenAPI/Swagger is intentionally not wired in M1 — the only shipping
  `Microsoft.OpenApi` builds had an open advisory (NU1903); re-add when a
  patched, source-generator-compatible version is available.
- `VideoUri` is read directly by the pipeline; for remote sources add a
  download step before analysis (future).
