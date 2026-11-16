"""Dump per-frame softmax of the CLIP 'safety' label group (same maths as YoloClipAnalyzer) for offline tuning.
    python eval/dump_safety_scores.py --videos eval/videos --out eval/results/raw-safety"""
import argparse, json, sys, time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))
import cv2  # noqa: E402
import torch  # noqa: E402
from PIL import Image  # noqa: E402

from advideo_pipeline.clip_cache import load_clip  # noqa: E402
from advideo_pipeline.device import pick_device  # noqa: E402
from advideo_pipeline.frames import probe_duration, sample_frames  # noqa: E402
from advideo_pipeline.taxonomy import CLIP_NEUTRAL_LABELS, CLIP_SCENE_LABELS  # noqa: E402

ap = argparse.ArgumentParser()
ap.add_argument("--videos", default=str(HERE / "videos")); ap.add_argument("--out", default=str(HERE / "results" / "raw-safety"))
ap.add_argument("--only", action="append")
a = ap.parse_args()
out = Path(a.out); out.mkdir(parents=True, exist_ok=True)
dev = pick_device()
clip, pre, tok, scale = load_clip("ViT-B-32", "laion2b_s34b_b79k", dev)
labels = [l for l, k in CLIP_SCENE_LABELS if k == "safety"]
with torch.no_grad():
    tf = clip.encode_text(tok([f"a photo of {l}" for l in labels]).to(dev)); tf = tf / tf.norm(dim=-1, keepdim=True)
samples = json.loads((HERE / "samples.json").read_text(encoding="utf-8"))["samples"]
for s in samples:
    if a.only and s["id"] not in a.only: continue
    dst = out / f"{s['id']}.json"
    if dst.exists(): continue
    p = str(Path(a.videos) / f"{s['id']}.mp4"); t0 = time.perf_counter(); rows = []
    for fr in sample_frames(p, probe_duration(p), fps=1.0, decode=True):
        with torch.no_grad():
            f = clip.encode_image(pre(Image.fromarray(cv2.cvtColor(fr.image, cv2.COLOR_BGR2RGB))).unsqueeze(0).to(dev))
            f = f / f.norm(dim=-1, keepdim=True)
            pr = (scale * f @ tf.T).softmax(dim=-1).squeeze(0).tolist()
        rows.append({"t": fr.time_sec, "scores": {l: round(v, 3) for l, v in zip(labels, pr) if v >= 0.05 and l not in CLIP_NEUTRAL_LABELS}})
    dst.write_text(json.dumps(rows)); print(f"{s['id']:<24} {len(rows)} {time.perf_counter()-t0:.1f}s")
