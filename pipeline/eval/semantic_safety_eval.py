"""R8c: choose semantic-safety thresholds on the TUNING set, then report lexicon vs lexicon+semantic.

    python eval/semantic_safety_eval.py            # report with thresholds from taxonomy.json
    python eval/semantic_safety_eval.py --search   # grid-search thresholds on the tuning set only

Held-out sets (text_safety_holdout*.json) are only reported, never used for the search.
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.semantic_safety import SemanticSafety
from advideo_pipeline.textmatch import news_detections, safety_detections

HERE = Path(__file__).parent
TUNING = HERE / "text_safety_cases.json"
HOLDOUTS = [HERE / "text_safety_holdout.json", HERE / "text_safety_holdout3.json"]


def load(p: Path) -> list[dict]:
    return json.loads(p.read_text(encoding="utf-8"))["cases"]


def decide(text: str, sem_hit, high: float | None, medium: float | None) -> bool:
    dets = safety_detections(text, "audio", 0.9) + news_detections(text, "audio", 0.9)
    if sem_hit is not None and high is not None:
        sev = "high" if sem_hit.score >= high else "medium" if sem_hit.score >= medium else None
        if sev:
            from advideo_pipeline.models import Detection
            dets.append(Detection(term=f"speech: ~{sem_hit.subtype}", score=0.9, kind="safety_speech",
                                  category=sem_hit.subtype, categories=[sem_hit.subtype, "sensitive"], severity=sev))
    seg = aggregate_segments([(0.0, 5.0, d) for d in dets], 6.0, 6.0, min_support=2)[0]
    return "sensitive" in seg.categories


def metrics(cases, hits, high, medium) -> dict:
    tp = fp = fn = tn = 0
    errors = []
    for c, h in zip(cases, hits):
        got = decide(c["text"], h, high, medium)
        want = c["expect"] == "sensitive"
        if got and want: tp += 1
        elif got: fp += 1; errors.append(f"FP s={h.score if h else '-'} {c['text']}")
        elif want: fn += 1; errors.append(f"FN s={h.score if h else '-'} {c['text']}")
        else: tn += 1
    p = tp / (tp + fp) if tp + fp else 1.0
    r = tp / (tp + fn) if tp + fn else 1.0
    return {"tp": tp, "fp": fp, "fn": fn, "tn": tn, "precision": round(p, 3), "recall": round(r, 3), "errors": errors}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--search", action="store_true")
    ap.add_argument("--high", type=float, help="override thresholds.high")
    ap.add_argument("--medium", type=float, help="override thresholds.medium")
    args = ap.parse_args()
    sem = SemanticSafety()
    tuning = load(TUNING)
    t_hits = sem.classify([c["text"] for c in tuning])

    high = args.high if args.high is not None else sem.high
    medium = args.medium if args.medium is not None else sem.medium
    if args.search:
        # semantic-only, on the tuning set: thresholds sit a fixed margin above the highest score of a SAFE case
        # (high must never fire on everyday speech; medium only counts together with another hit)
        safe = [h.score for c, h in zip(tuning, t_hits) if c["expect"] != "sensitive"]
        top_safe = max(safe) if safe else 0.0
        high, medium = round(top_safe + 0.12, 2), round(top_safe + 0.05, 2)
        print(f"tuning set: highest safe score {top_safe:.3f} -> high={high} medium={medium}")

    out = {"thresholds": {"high": high, "medium": medium}}
    for name, cases, hits in [("tuning", tuning, t_hits)] + [
        (p.name, load(p), None) for p in HOLDOUTS if p.exists()
    ]:
        if hits is None:
            hits = sem.classify([c["text"] for c in cases])
        lex = metrics(cases, hits, None, None)
        comb = metrics(cases, hits, high, medium)
        out[name] = {"lexicon": {k: v for k, v in lex.items() if k != "errors"},
                     "lexicon+semantic": {k: v for k, v in comb.items() if k != "errors"},
                     "errors": comb["errors"]}
    print(json.dumps({k: ({kk: vv for kk, vv in v.items() if kk != "errors"} if isinstance(v, dict) else v)
                      for k, v in out.items()}, indent=1))
    for name in [k for k in out if k != "thresholds"]:
        for e in out[name]["errors"]:
            print(name, e)
    return 0


if __name__ == "__main__":
    sys.exit(main())
