"""Aggregate timed detections into contiguous, non-overlapping segments.

Strategy (v1): fixed-width windows of `window_sec`. Every detection carries a
time range [start, end]; frame-level detections are points (start == end),
track-level detections (e.g. audio/Whisper) span a range. A detection is added
to every window its range overlaps. Within a window we merge detections, keep
the max score per term, and split them into the keywords / entities /
categories / signals shape of the JSON map.

A later version can replace fixed windows with scene-cut boundaries without
changing the output contract.
"""

from __future__ import annotations

from collections import defaultdict

from .models import Detection, Segment

TimedDetection = tuple[float, float, Detection]

_SUPPORT_KINDS = {"object", "action", "scene"}
_SIGNAL_KINDS = {"object": "objects", "action": "actions", "text": "text",
                 "audio": "audioKeywords"}


def aggregate_segments(
    timed_dets: list[TimedDetection],
    duration_sec: float,
    window_sec: float = 6.0,
    min_support: int = 1,
    strong_score: float = 0.8,
) -> list[Segment]:
    """Group timed detections into windows.

    Temporal support filter (visual classifier kinds object/action/scene only): a term is kept in a
    window when it was seen in at least `min_support` sampled frames of that window, or once with
    score >= `strong_score`. One noisy frame (e.g. CLIP "running" 0.4 on a glass of soda) then does
    not label 6 seconds. Text (OCR) and audio detections are exact lexicon matches and range
    detections come from track analyzers (Whisper), so both bypass the filter.
    """
    if window_sec <= 0:
        raise ValueError("window_sec must be > 0")

    n_windows = max(1, -(-int(duration_sec) // int(window_sec)))  # ceil

    # assign each detection to every window its [start, end] overlaps
    # (detection, exempt): range detections (track analyzers) and text/audio bypass the support filter
    buckets: dict[int, list[tuple[Detection, bool]]] = defaultdict(list)
    for start, end, det in timed_dets:
        first = int(start // window_sec)
        last = int(max(start, end - 1e-9) // window_sec)  # inclusive
        for idx in range(max(0, first), min(last, n_windows - 1) + 1):
            buckets[idx].append((det, end > start or det.kind not in _SUPPORT_KINDS))

    if min_support > 1:
        for idx, dets in list(buckets.items()):
            buckets[idx] = _filter_support(dets, min_support, strong_score)

    segments: list[Segment] = []
    for idx in range(n_windows):
        start = idx * window_sec
        end = min((idx + 1) * window_sec, duration_sec)
        if end <= start:
            continue
        segments.append(_build_segment(start, end, [d for d, _ in buckets.get(idx, [])]))
    return segments


def _filter_support(
    dets: list[tuple[Detection, bool]], min_support: int, strong_score: float
) -> list[tuple[Detection, bool]]:
    counts: dict[str, int] = defaultdict(int)
    best: dict[str, float] = defaultdict(float)
    for d, exempt in dets:
        if not exempt:
            counts[d.term] += 1
            best[d.term] = max(best[d.term], d.score)
    keep = {t for t in counts if counts[t] >= min_support or best[t] >= strong_score}
    return [(d, r) for d, r in dets if r or d.term in keep]


def _build_segment(start: float, end: float, dets: list[Detection]) -> Segment:
    kw_score: dict[str, float] = {}
    ent: dict[tuple[str, str], float] = {}
    categories: set[str] = set()
    signals: dict[str, set[str]] = defaultdict(set)
    scene: str | None = None

    for d in dets:
        kw_score[d.term] = max(kw_score.get(d.term, 0.0), d.score)
        if d.category:
            categories.add(d.category)
        categories.update(d.categories)
        if d.entity_type and d.entity_label:
            key = (d.entity_type, d.entity_label)
            ent[key] = max(ent.get(key, 0.0), d.score)
        if d.kind in _SIGNAL_KINDS:
            signals[_SIGNAL_KINDS[d.kind]].add(d.term)
        elif d.kind == "scene":
            scene = d.term

    keywords = [
        {"term": t, "score": round(s, 3)}
        for t, s in sorted(kw_score.items(), key=lambda x: -x[1])
    ]
    entities = [
        {"type": et, "label": el, "score": round(s, 3)}
        for (et, el), s in sorted(ent.items(), key=lambda x: -x[1])
    ]
    sig = {k: sorted(v) for k, v in signals.items()}
    if scene:
        sig["scene"] = scene

    return Segment(
        start_sec=start,
        end_sec=end,
        keywords=keywords,
        entities=entities,
        categories=sorted(categories),
        signals=sig,
    )
