# advideo orchestrator (.NET) — M1 stub

Owns (later work): ingest API for the platform's video list, job queue,
scheduling/dedup/retries, result storage, and — from M2 — the ad-serving API.

For M1 the running artifact is the Python `pipeline/`. This stub records the
intended boundary: the orchestrator enqueues jobs `{ videoId, videoUri }` and
workers return the JSON content map (`../schema/video-map.schema.json`) to the
object store. To be scaffolded as an ASP.NET Core solution in a later task.
