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
