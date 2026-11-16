"""Compare eval results with baseline.json; exit 1 on a regression (CI-style guard for taxonomy/threshold changes).

    python eval/run_eval.py --config yolo+clip --config "ocr,clip-brand"     # writes eval/results/*.json
    python eval/check_regression.py [--results eval/results] [--update]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent


def _get(d: dict, key: str):
    """'perCategory.sensitive.precision' -> nested lookup."""
    for part in key.split("."):
        if not isinstance(d, dict) or part not in d:
            return None
        d = d[part]
    return d


def tag(cfg: str, ms: int = 2) -> str:
    return cfg.replace("+", "_").replace(",", "__") + f"__ms{ms}"


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--results", default=str(HERE / "results"))
    ap.add_argument("--baseline", default=str(HERE / "baseline.json"))
    ap.add_argument("--update", action="store_true", help="write current metrics into the baseline")
    a = ap.parse_args(argv)
    base = json.loads(Path(a.baseline).read_text(encoding="utf-8"))
    tol = base["tolerance"]
    failures, lines = [], []
    for cfg, ref in base["configs"].items():
        f = Path(a.results) / f"{tag(cfg)}.json"
        if not f.exists():
            failures.append(f"{cfg}: no results ({f.name}) - run run_eval.py --config '{cfg}'")
            continue
        cur = json.loads(f.read_text(encoding="utf-8"))
        n = len(cur.get("videos", []))
        if n != ref.get("clips", n):
            lines.append(f"  note {cfg}: {n} clips evaluated, baseline has {ref['clips']}")
        for k, v in ref.items():
            c = _get(cur, k)
            if k == "clips" or c is None:
                continue
            if k == "falseBrands":
                bad = c > v + tol["falseBrandsIncrease"]
            elif k in ("brandRecall", "perCategory.sensitive.recall", "perCategory.sensitive.precision"):
                bad = c < v - tol["brandRecallDrop"]
            else:
                bad = c < v - tol["drop"]
            lines.append(f"  {'FAIL' if bad else 'ok  '} {cfg:<16} {k:<18} baseline {v:<6} now {c}")
            if bad:
                failures.append(f"{cfg} {k}: {v} -> {c}")
            if a.update:
                ref[k] = round(c, 2) if isinstance(c, float) else c
        if a.update:
            ref["clips"] = n
    print("\n".join(lines))
    if a.update:
        Path(a.baseline).write_text(json.dumps(base, indent=2) + "\n", encoding="utf-8")
        print("baseline updated")
        return 0
    if failures:
        print("REGRESSION:\n  " + "\n  ".join(failures))
        return 1
    print("no regression")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
