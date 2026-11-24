"""R8d: brand-safety evaluation on full real broadcasts (story-level labels).

Inputs per broadcast (``--transcripts DIR``): a Whisper transcript ``<id>.json`` = list of
``{"start", "end", "text"}`` segments. Transcripts and media are NOT committed (broadcast content is
copyrighted); only the story-level labels in ``eval/broadcast/<id>.labels.json`` are.

Speech detections are produced exactly like ``WhisperAudioAnalyzer`` (lexicon ``safety_detections`` +
optional semantic classifier) and aggregated into 6 s windows with the production rules
(``aggregate_segments``). A window is *flagged* when it carries the ``sensitive`` category.

Metrics (ambiguous stories ``A`` are not scored):
* window recall    – share of windows inside sensitive stories (``S``) that are flagged
* story recall     – share of ``S`` stories with at least one flagged window
* false-block rate – share of windows inside safe stories (``N``) that are flagged (lost ad inventory)
* false stories    – ``N`` stories with at least one flagged window

Usage::

    python eval/broadcast_safety_eval.py --transcripts ~/eval/r8d/tr [--modes lexicon,semantic] [--json out.json]
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent / "src"))

from advideo_pipeline.aggregate import aggregate_segments  # noqa: E402
from advideo_pipeline.textmatch import safety_detections  # noqa: E402


def detections(segments: list[dict], semantic) -> list[tuple[float, float, object]]:
    out = []
    sem = semantic.detections([s["text"] for s in segments]) if semantic else [[] for _ in segments]
    for seg, sd in zip(segments, sem):
        for d in list(sd) + safety_detections(seg["text"], kind="audio", score=0.8):
            out.append((float(seg["start"]), float(seg["end"]), d))
    return out


def label_at(stories: list[dict], t: float) -> dict | None:
    for s in stories:
        if s["start"] <= t < s["end"]:
            return s
    return None


def evaluate(labels: dict, segments: list[dict], semantic, context: int = 1) -> dict:
    windows = aggregate_segments(detections(segments, semantic), labels["durationSec"], text_safety_context=context)
    stats = {"S": [0, 0], "N": [0, 0]}  # [flagged, total]
    story_hit: dict[int, bool] = {}
    false_examples = []
    for w in windows:
        story = label_at(labels["stories"], (w.start_sec + w.end_sec) / 2)
        if story is None or story["label"] not in stats:
            continue
        flagged = "sensitive" in w.categories
        stats[story["label"]][0] += flagged
        stats[story["label"]][1] += 1
        key = labels["stories"].index(story)
        story_hit[key] = story_hit.get(key, False) or flagged
        if flagged and story["label"] == "N":
            false_examples.append({"t": w.start_sec, "story": story["topic"], "signals": w.signals.get("safety", [])})
    s_idx = [i for i, s in enumerate(labels["stories"]) if s["label"] == "S"]
    n_idx = [i for i, s in enumerate(labels["stories"]) if s["label"] == "N"]
    return {
        "windowRecall": round(stats["S"][0] / max(1, stats["S"][1]), 3),
        "storyRecall": f"{sum(story_hit.get(i, False) for i in s_idx)}/{len(s_idx)}",
        "falseBlockRate": round(stats["N"][0] / max(1, stats["N"][1]), 3),
        "falseStories": f"{sum(story_hit.get(i, False) for i in n_idx)}/{len(n_idx)}",
        "windows": {"S": stats["S"], "N": stats["N"]},
        "missedStories": [labels["stories"][i]["topic"] for i in s_idx if not story_hit.get(i, False)],
        "falseBlocks": false_examples,
    }


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--transcripts", required=True, help="directory with <id>.json Whisper transcripts")
    ap.add_argument("--labels", default=str(HERE / "broadcast"), help="directory with <id>.labels.json")
    ap.add_argument("--modes", default="lexicon,semantic")
    ap.add_argument("--json", help="write results to this file")
    ap.add_argument("--context", type=int, default=1, help="neighbouring windows used for corroboration (0 = R8b rule)")
    ap.add_argument("--sem-high", type=float, help="override semantic high threshold")
    ap.add_argument("--sem-medium", type=float, help="override semantic medium threshold")
    args = ap.parse_args(argv)

    semantic = None
    results: dict = {}
    for mode in args.modes.split(","):
        if mode == "semantic" and semantic is None:
            from advideo_pipeline.semantic_safety import SemanticSafety
            semantic = SemanticSafety(high=args.sem_high, medium=args.sem_medium)
        for lf in sorted(Path(args.labels).glob("*.labels.json")):
            labels = json.loads(lf.read_text())
            tf = Path(args.transcripts).expanduser() / f"{labels['id']}.json"
            if not tf.exists():
                print(f"skip {labels['id']}: no transcript", file=sys.stderr)
                continue
            r = evaluate(labels, json.loads(tf.read_text()), semantic if mode == "semantic" else None, args.context)
            results.setdefault(labels["id"], {})[mode] = r
            print(f"{labels['id']:24s} {mode:9s} windowRecall={r['windowRecall']:.2f} storyRecall={r['storyRecall']} "
                  f"falseBlockRate={r['falseBlockRate']:.3f} falseStories={r['falseStories']}")
            for m in r["missedStories"]:
                print(f"    missed: {m}")
            for fb in r["falseBlocks"][:8]:
                print(f"    false block @{fb['t']:.0f}s [{fb['story']}] {fb['signals']}")
    if args.json:
        Path(args.json).write_text(json.dumps(results, ensure_ascii=False, indent=1))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
