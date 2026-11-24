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

_SUPPORT_KINDS = {"object", "action", "scene", "logo", "safety"}
_SIGNAL_KINDS = {"object": "objects", "action": "actions", "text": "text", "logo": "logos", "safety": "safety",
                 "audio": "audioKeywords", "safety_speech": "safety", "safety_ocr": "safety", "news": "news"}
# brand-safety hits from speech/on-screen text: decided per window by severity (see _apply_text_safety)
_TEXT_SAFETY_KINDS = {"safety_speech", "safety_ocr"}
# cues that only label the segment (categories + signals), never ad-matching keywords
_NO_KEYWORD_KINDS = _TEXT_SAFETY_KINDS | {"news", "context"}
# R8f sport context: picture of a match counts like a spoken/written sport marker
_VISUAL_SPORT_TERMS = {"playing football", "playing tennis", "playing basketball", "a stadium", "a sports field",
                       "sports ball", "tennis racket"}


def aggregate_segments(
    timed_dets: list[TimedDetection],
    duration_sec: float,
    window_sec: float = 6.0,
    min_support: int = 1,
    strong_score: float = 0.8,
    logo_min_support: int = 3,
    logo_strong_score: float = 0.97,
    text_safety_min_hits: int = 2,
    text_safety_context: int = 1,
    sport_context: int = 3,
    sport_min_markers: int = 2,
) -> list[Segment]:
    """Group timed detections into windows.

    Temporal support filter (visual classifier kinds object/action/scene only): a term is kept in a
    window when it was seen in at least `min_support` sampled frames of that window, or once with
    score >= `strong_score`. One noisy frame (e.g. CLIP "running" 0.4 on a glass of soda) then does
    not label 6 seconds. Text (OCR) and audio detections are exact lexicon matches and range
    detections come from track analyzers (Whisper), so both bypass the filter.
    Logos (kind "logo") use stricter `logo_min_support` / `logo_strong_score`: a false brand would show the
    wrong advertiser, so a logo must be seen in >= 3 frames of the window (or once almost certainly).
    Brand-safety hits from speech/OCR (R8b) count only if the window has one high-severity hit or at least
    `text_safety_min_hits` different medium/high hits ("killer feature" or one "attack" alone does not block ads).
    R8d: medium hits may be corroborated by hits in the `text_safety_context` neighbouring windows on each side
    (default 1 -> an 18 s span): in real news a story spreads its cues over several sentences ("zranený" … "smrť").
    R8f: in sport context (>= `sport_min_markers` distinct sport cues — "zápas", "gól", NBA, a stadium in the picture —
    within `sport_context` windows on each side) sport-ambiguous safety words ("streľba", "útočník", "zranený",
    "masaker") are dropped. War-politics words support a sensitive hit but are not sensitive alone.
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
            buckets[idx] = _filter_support(dets, min_support, strong_score, logo_min_support, logo_strong_score)
    markers = {idx: _sport_markers(dets) for idx, dets in buckets.items()}
    for idx, dets in list(buckets.items()):
        near = set().union(*(markers.get(j, set()) for j in range(idx - sport_context, idx + sport_context + 1)))
        if len(near) >= sport_min_markers:
            buckets[idx] = [(d, r) for d, r in dets if "sport" not in d.ambiguous_in]
    context = {idx: _text_hits(dets) for idx, dets in buckets.items()}
    for idx, dets in list(buckets.items()):
        near = [h for j in range(idx - text_safety_context, idx + text_safety_context + 1) for h in context.get(j, [])]
        buckets[idx] = _apply_text_safety(dets, text_safety_min_hits, near)

    segments: list[Segment] = []
    for idx in range(n_windows):
        start = idx * window_sec
        end = min((idx + 1) * window_sec, duration_sec)
        if end <= start:
            continue
        segments.append(_build_segment(start, end, [d for d, _ in buckets.get(idx, [])]))
    return segments


def _filter_support(
    dets: list[tuple[Detection, bool]], min_support: int, strong_score: float,
    logo_min_support: int = 3, logo_strong_score: float = 0.97,
) -> list[tuple[Detection, bool]]:
    counts: dict[str, int] = defaultdict(int)
    best: dict[str, float] = defaultdict(float)
    for d, exempt in dets:
        if not exempt:
            counts[d.term] += 1
            best[d.term] = max(best[d.term], d.score)
    logos = {d.term for d, _ in dets if d.kind == "logo"}
    safety = {d.term for d, _ in dets if d.kind == "safety"}

    def ok(t: str) -> bool:
        if t in safety:  # brand safety: always >= 2 frames, no single-frame bypass (a false block costs revenue)
            return counts[t] >= max(2, min_support)
        ms, ss = (logo_min_support, logo_strong_score) if t in logos else (min_support, strong_score)
        return counts[t] >= ms or best[t] >= ss

    keep = {t for t in counts if ok(t)}
    return [(d, r) for d, r in dets if r or d.term in keep]


def _phrase(d: Detection) -> str:
    """Corroboration key: the matched phrase without source prefix; all semantic hits ("~war") share one key,
    so the embedding classifier never corroborates itself (it repeats on routine police/court talk)."""
    p = d.term.split(": ", 1)[-1]
    return "~semantic" if p.startswith("~") else p


def _text_hits(dets: list[tuple[Detection, bool]]) -> list[Detection]:
    return [d for d, _ in dets if d.kind in _TEXT_SAFETY_KINDS and d.severity in ("medium", "high")]


def _sport_markers(dets: list[tuple[Detection, bool]]) -> set[str]:
    out = {d.term for d, _ in dets if d.kind == "context" and d.term.startswith("sport: ")}
    out |= {f"visual: {d.term}" for d, _ in dets if d.kind in ("action", "scene", "object") and d.term in _VISUAL_SPORT_TERMS}
    return out


def _apply_text_safety(dets: list[tuple[Detection, bool]], min_hits: int,
                       context: list[Detection] | None = None) -> list[tuple[Detection, bool]]:
    """Drop medium-severity speech/OCR safety hits unless corroborated.

    low (profanity) is always kept (never sensitive). Sensitive hits and non-sensitive ones (drugs, war politics) are
    corroborated separately: a group is kept when the window has a high-severity hit of it, or the window has a hit
    and the window plus its `context` (hits of the neighbouring windows) hold >= `min_hits` distinct matched phrases
    (source prefix ignored, so the same word in speech and on screen counts once). A drugs word therefore never turns
    a lone "victims" into a sensitive window.

    Sensitive corroboration may also use war-politics words (``supports_sensitive``: "vojna" + "útok" is sensitive),
    but then at least one sensitive *lexicon* phrase must be among them — "vojna" + "armáda" or "vojna" + a semantic
    "~war" stay non-sensitive (category war; blocked only by the strict preset).
    """
    def group(d: Detection) -> bool:
        return "sensitive" in d.categories

    text_hits = _text_hits(dets)
    if not text_hits:
        return dets
    pool = text_hits + list(context or [])
    keep_group: dict[bool, bool] = {}

    own = [d for d in text_hits if group(d)]
    sens_keys = {_phrase(d) for d in pool if group(d)}
    support_keys = {_phrase(d) for d in pool if d.supports_sensitive and not group(d)}
    lexicon_sens = {k for k in sens_keys if k != "~semantic"}
    keep_group[True] = bool(own) and (
        any(d.severity == "high" for d in own)
        or (len(sens_keys | support_keys) >= max(1, min_hits) and bool(lexicon_sens))
        or len(sens_keys) >= max(1, min_hits)  # several distinct sensitive phrases (only one may be semantic)
    )
    own_ns = [d for d in text_hits if not group(d)]
    ns_keys = {_phrase(d) for d in pool if not group(d)}
    if any(d.supports_sensitive for d in own_ns) and any(_phrase(d) == "~semantic" and "war" in d.categories for d in pool):
        ns_keys.add("~semantic")  # "army" + semantic "~war": war politics (strict), not sensitive
    keep_group[False] = bool(own_ns) and (any(d.severity == "high" for d in own_ns) or len(ns_keys) >= max(1, min_hits)
                                           or (keep_group[True] and all(d.supports_sensitive for d in own_ns)))
    return [(d, r) for d, r in dets
            if not (d.kind in _TEXT_SAFETY_KINDS and d.severity in ("medium", "high")) or keep_group[group(d)]]


def _build_segment(start: float, end: float, dets: list[Detection]) -> Segment:
    kw_score: dict[str, float] = {}
    ent: dict[tuple[str, str], float] = {}
    categories: set[str] = set()
    signals: dict[str, set[str]] = defaultdict(set)
    scene: str | None = None

    for d in dets:
        if d.kind not in _NO_KEYWORD_KINDS:
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
