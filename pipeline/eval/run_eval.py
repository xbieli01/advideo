"""Evaluate real analyzers on the eval clips against manual ground truth.

    python eval/run_eval.py --videos eval/videos --config yolo+clip --config yolo+clip,ocr+whisper
    (config = frame analyzers comma-separated, optional "+<track>" suffix, e.g. "yolo+clip,ocr+whisper")

Per 6 s window (same as the content map) it compares predicted vs expected:
- categories: precision / recall (what the ad matcher targets); "allowed" categories are neutral
- brands: recall + false brands
- terms: recall of expected keywords (termsAny = any of the listed terms)
- speed: model load time, processing seconds, realtime factor (processing / video duration)
Writes eval/results/<config>.json and a markdown summary to stdout (and --report file).
"""

from __future__ import annotations

import argparse
import json
import platform
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))

from advideo_pipeline.analyzers import get_analyzer, get_track_analyzer  # noqa: E402
from advideo_pipeline.pipeline import analyze_video  # noqa: E402


def parse_config(cfg: str) -> tuple[list[str], list[str]]:
    # "yolo+clip,ocr+whisper" -> frame [yolo+clip, ocr], track [whisper]; "yolo+clip" stays a frame analyzer
    frame, track = [], []
    for part in cfg.split(","):
        part = part.strip()
        for name in ("whisper",):
            if part.endswith("+" + name):
                track.append(name)
                part = part[: -len(name) - 1]
            elif part == name:
                track.append(name)
                part = ""
        if part:
            frame.append(part)
    return frame, track


def parse_opts(items: list[str]) -> dict[str, dict]:
    out: dict[str, dict] = {}
    for item in items:
        key, _, raw = item.partition("=")
        name, _, field = key.rpartition(".")
        try:
            val = json.loads(raw)
        except json.JSONDecodeError:
            val = raw
        out.setdefault(name, {})[field] = val
    return out


def expected_for(gt: list[dict], start: float, end: float) -> dict:
    exp = {"categories": set(), "allowed": set(), "brands": set(), "terms": [], "covered": False}
    for r in gt:
        if r["start"] < end and r["end"] > start:
            exp["covered"] = True
            exp["categories"] |= set(r.get("categories", []))
            exp["allowed"] |= set(r.get("allowed", []))
            exp["brands"] |= set(r.get("brands", []))
            groups = [set(r["termsAny"])] if r.get("termsAny") else [{t} for t in r.get("terms", [])]
            for g in groups:
                if g not in exp["terms"]:
                    exp["terms"].append(g)
    return exp


def score_video(vmap: dict, gt: list[dict]) -> dict:
    c = {"cat_tp": 0, "cat_fp": 0, "cat_fn": 0, "brand_tp": 0, "brand_fn": 0, "brand_fp": 0,
         "term_hit": 0, "term_total": 0, "windows": 0, "fp_cats": {}, "missed_cats": {}, "missed_terms": {}}
    for seg in vmap["segments"]:
        exp = expected_for(gt, seg["startSec"], seg["endSec"])
        if not exp["covered"]:
            continue
        c["windows"] += 1
        pred_cats = set(seg["categories"])
        kws = {k["term"].lower() for k in seg["keywords"]}
        brands = {e["label"] for e in seg["entities"] if e["type"] == "brand"}
        tp = pred_cats & exp["categories"]
        fp = pred_cats - exp["categories"] - exp["allowed"]
        fn = exp["categories"] - pred_cats
        c["cat_tp"] += len(tp)
        c["cat_fp"] += len(fp)
        c["cat_fn"] += len(fn)
        for x in fp:
            c["fp_cats"][x] = c["fp_cats"].get(x, 0) + 1
        for x in fn:
            c["missed_cats"][x] = c["missed_cats"].get(x, 0) + 1
        c["brand_tp"] += len(brands & exp["brands"])
        c["brand_fn"] += len(exp["brands"] - brands)
        c["brand_fp"] += len(brands - exp["brands"])
        for group in exp["terms"]:
            c["term_total"] += 1
            if group & kws:
                c["term_hit"] += 1
            else:
                k = "|".join(sorted(group))
                c["missed_terms"][k] = c["missed_terms"].get(k, 0) + 1
    return c


def ratio(a: int, b: int) -> float | None:
    return round(a / b, 3) if b else None


def summarize(c: dict) -> dict:
    return {
        "categoryPrecision": ratio(c["cat_tp"], c["cat_tp"] + c["cat_fp"]),
        "categoryRecall": ratio(c["cat_tp"], c["cat_tp"] + c["cat_fn"]),
        "brandRecall": ratio(c["brand_tp"], c["brand_tp"] + c["brand_fn"]),
        "falseBrands": c["brand_fp"],
        "termRecall": ratio(c["term_hit"], c["term_total"]),
    }


def merge(total: dict, c: dict) -> None:
    for k, v in c.items():
        if isinstance(v, dict):
            for kk, vv in v.items():
                total.setdefault(k, {})[kk] = total.setdefault(k, {}).get(kk, 0) + vv
        else:
            total[k] = total.get(k, 0) + v


def fmt(v) -> str:
    return "–" if v is None else f"{v:.2f}"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--videos", default=str(HERE / "videos"))
    ap.add_argument("--config", action="append", required=True)
    ap.add_argument("--fps", type=float, default=1.0)
    ap.add_argument("--window-sec", type=float, default=6.0)
    ap.add_argument("--min-support", type=int, default=2, help="temporal support filter (1 = off)")
    ap.add_argument("--only", action="append", help="sample id(s) to run")
    ap.add_argument("--opt", action="append", default=[],
                    help="analyzer option, e.g. yolo+clip.clip_threshold=0.35 (repeatable)")
    ap.add_argument("--results", default=str(HERE / "results"))
    ap.add_argument("--report", help="append the markdown summary to this file")
    ap.add_argument("--cache", action="store_true",
                    help="reuse per-video maps from <results>/maps/<tag>/ (lets long CPU runs proceed in steps)")
    args = ap.parse_args(argv)

    samples = json.loads((HERE / "samples.json").read_text(encoding="utf-8"))["samples"]
    if args.only:
        samples = [s for s in samples if s["id"] in args.only]
    Path(args.results).mkdir(parents=True, exist_ok=True)
    lines = [f"## Eval {time.strftime('%Y-%m-%d %H:%M')} — {platform.machine()} {platform.system()}, fps={args.fps}", ""]

    for cfg in args.config:
        frame_names, track_names = parse_config(cfg)
        tag = cfg.replace('+', '_').replace(',', '__') + f"__ms{args.min_support}" + "".join(
            f"__{o.partition('=')[0].rsplit('.', 1)[-1]}-{o.partition('=')[2]}" for o in args.opt)
        maps_dir = Path(args.results) / "maps" / tag
        maps_dir.mkdir(parents=True, exist_ok=True)
        cached = {p.stem: json.loads(p.read_text(encoding="utf-8")) for p in maps_dir.glob("*.json")} if args.cache else {}
        t0 = time.perf_counter()
        opts = parse_opts(args.opt)
        need_models = not all(s["id"] in cached for s in samples)
        frames = [get_analyzer(n, **opts.get(n, {})) for n in frame_names] if need_models else []
        tracks = [get_track_analyzer(n, **opts.get(n, {})) for n in track_names] if need_models else []
        load_s = time.perf_counter() - t0 if need_models else float(next(iter(cached.values()), {}).get("loadSec", 0))
        device = getattr(frames[0], "device", "?") if frames else (next(iter(cached.values()), {}).get("device", "cached"))
        total: dict = {}
        per_video = []
        proc_total = dur_total = 0.0
        for s in samples:
            path = Path(args.videos) / f"{s['id']}.mp4"
            if s["id"] in cached:
                vmap, proc = cached[s["id"]]["map"], cached[s["id"]]["processingSec"]
            else:
                t1 = time.perf_counter()
                vmap = analyze_video(str(path), s["id"], analyzer=list(frames), track=list(tracks),
                                     fps=args.fps, window_sec=args.window_sec,
                                     min_support=args.min_support).to_json()
                proc = time.perf_counter() - t1
                (maps_dir / f"{s['id']}.json").write_text(json.dumps({"processingSec": proc, "device": getattr(frames[0], "device", "?") if frames else "?", "map": vmap}), encoding="utf-8")
            proc_total += proc
            dur_total += vmap["durationSec"]
            c = score_video(vmap, s["groundTruth"])
            merge(total, c)
            per_video.append({"id": s["id"], "durationSec": vmap["durationSec"], "processingSec": round(proc, 2),
                              **summarize(c), "missedCategories": c["missed_cats"], "falseCategories": c["fp_cats"],
                              "missedTerms": c["missed_terms"],
                              "segments": [{"t": f"{g['startSec']:.0f}-{g['endSec']:.0f}", "categories": g["categories"],
                                            "keywords": [f"{k['term']}:{k['score']:.2f}" for k in g["keywords"][:8]],
                                            "brands": [e["label"] for e in g["entities"] if e["type"] == "brand"]}
                                           for g in vmap["segments"]]})
            print(f"  {cfg:<28} {s['id']:<18} {proc:6.1f}s", file=sys.stderr)
        overall = summarize(total)
        rtf = proc_total / dur_total if dur_total else 0
        result = {"config": cfg, "options": args.opt, "minSupport": args.min_support, "device": device, "fps": args.fps, "modelLoadSec": round(load_s, 1),
                  "processingSec": round(proc_total, 1), "videoSec": round(dur_total, 1), "realtimeFactor": round(rtf, 2),
                  **overall, "falseCategories": total.get("fp_cats", {}), "missedCategories": total.get("missed_cats", {}),
                  "missedTerms": total.get("missed_terms", {}), "videos": per_video}
        (Path(args.results) / f"{tag}.json").write_text(
            json.dumps(result, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

        opt_txt = (f" {' '.join(args.opt)}" if args.opt else "") + f" min_support={args.min_support}"
        lines += [f"### `{cfg}`{opt_txt} (device {device})", "",
                  f"categories P/R **{fmt(overall['categoryPrecision'])} / {fmt(overall['categoryRecall'])}**, "
                  f"terms R {fmt(overall['termRecall'])}, brands R {fmt(overall['brandRecall'])} (false brands {overall['falseBrands']}); "
                  f"load {load_s:.0f}s, {proc_total:.0f}s for {dur_total:.0f}s video = **{rtf:.2f}× realtime**", "",
                  "| video | cat P | cat R | term R | brand R | s | missed | false |", "|---|---|---|---|---|---|---|---|"]
        for v in per_video:
            lines.append(f"| {v['id']} | {fmt(v['categoryPrecision'])} | {fmt(v['categoryRecall'])} | {fmt(v['termRecall'])} | "
                         f"{fmt(v['brandRecall'])} | {v['processingSec']:.0f} | {', '.join(v['missedCategories']) or '–'} | "
                         f"{', '.join(v['falseCategories']) or '–'} |")
        lines.append("")
    md = "\n".join(lines)
    print(md)
    if args.report:
        with open(args.report, "a", encoding="utf-8") as fh:
            fh.write(md + "\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
