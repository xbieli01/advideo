"""R8b: precision/recall of brand safety from speech/on-screen text on hand-written transcripts.

    python eval/run_text_safety_eval.py            # report
    python eval/run_text_safety_eval.py --strict   # exit 1 if below the thresholds used by the test
One case = one 6 s window; it is "sensitive" when the aggregated segment has category "sensitive".
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import defaultdict
from pathlib import Path

from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.textmatch import news_detections, safety_detections

CASES = Path(__file__).with_name("text_safety_cases.json")
MIN_PRECISION = 0.95
MIN_RECALL = 0.9


def evaluate(cases_path: Path = CASES) -> dict:
    cases = json.loads(cases_path.read_text(encoding="utf-8"))["cases"]
    per_lang: dict[str, dict[str, int]] = defaultdict(lambda: {"tp": 0, "fp": 0, "fn": 0, "tn": 0})
    failures: list[str] = []
    for c in cases:
        dets = safety_detections(c["text"], "audio", 0.9) + news_detections(c["text"], "audio", 0.9)
        seg = aggregate_segments([(0.0, 5.0, d) for d in dets], duration_sec=6.0, window_sec=6.0, min_support=2)[0]
        got = "sensitive" in seg.categories
        want = c["expect"] == "sensitive"
        key = ("tp" if want else "fp") if got else ("fn" if want else "tn")
        per_lang[c["lang"]][key] += 1
        missing = [cat for cat in c.get("categories", []) if cat not in seg.categories]
        news_bad = c.get("news") and "news" not in seg.categories
        if got != want or missing or news_bad:
            failures.append(f"[{c['lang']}] expect={c['expect']} got={'sensitive' if got else 'safe'} "
                            f"cats={seg.categories} missing={missing}{' news-missing' if news_bad else ''} :: {c['text']}")
    tot = {k: sum(v[k] for v in per_lang.values()) for k in ("tp", "fp", "fn", "tn")}

    def pr(m):
        p = m["tp"] / (m["tp"] + m["fp"]) if m["tp"] + m["fp"] else 1.0
        r = m["tp"] / (m["tp"] + m["fn"]) if m["tp"] + m["fn"] else 1.0
        return round(p, 3), round(r, 3)

    return {"cases": len(cases), "total": {**tot, "precision": pr(tot)[0], "recall": pr(tot)[1]},
            "perLang": {k: {**v, "precision": pr(v)[0], "recall": pr(v)[1]} for k, v in sorted(per_lang.items())},
            "failures": failures}


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--strict", action="store_true")
    args = ap.parse_args()
    res = evaluate()
    print(json.dumps({k: v for k, v in res.items() if k != "failures"}, indent=2))
    for f in res["failures"]:
        print("FAIL", f)
    ok = res["total"]["precision"] >= MIN_PRECISION and res["total"]["recall"] >= MIN_RECALL
    return 0 if ok or not args.strict else 1


if __name__ == "__main__":
    sys.exit(main())
