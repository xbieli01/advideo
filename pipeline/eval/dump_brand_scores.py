"""Dump per-frame brand probabilities of the clip-brand analyzer (threshold 0) for offline tuning.
    python eval/dump_brand_scores.py --videos eval/videos --out eval/raw [--only id ...]"""
import argparse, json, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))
from advideo_pipeline.analyzers.clip_brand import ClipBrandAnalyzer  # noqa: E402
from advideo_pipeline.frames import probe_duration, sample_frames  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--videos", default=str(HERE / "videos")); ap.add_argument("--out", default=str(HERE / "results" / "raw"))
ap.add_argument("--only", action="append"); ap.add_argument("--fps", type=float, default=1.0)
a = ap.parse_args()
out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
samples = json.loads((HERE / "samples.json").read_text(encoding="utf-8"))["samples"]
an = ClipBrandAnalyzer(threshold=0.0, interval_sec=0.0)
for s in samples:
    if a.only and s["id"] not in a.only: continue
    dst = out / f"{s['id']}.json"
    if dst.exists(): continue
    p = str(Path(a.videos) / f"{s['id']}.mp4"); t0 = time.perf_counter(); rows = []
    for fr in sample_frames(p, probe_duration(p), fps=a.fps, decode=True):
        rows.append({"t": fr.time_sec, "scores": {d.term: d.score for d in an.analyze(fr) if d.score >= 0.05}})
    dst.write_text(json.dumps(rows)); print(f"{s['id']:<24} {len(rows)} frames {time.perf_counter()-t0:.1f}s")
