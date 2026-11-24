"""R8c: semantic text safety — window rule integration with a stub classifier, plus an optional real-model test."""

from __future__ import annotations

import importlib.util
import os

import pytest

from advideo_pipeline.aggregate import aggregate_segments
from advideo_pipeline.models import Detection
from advideo_pipeline.semantic_safety import SemanticHit, enabled_by_env
from advideo_pipeline.textmatch import safety_detections


def _sem(subtype: str, severity: str) -> Detection:
    return Detection(term=f"speech: ~{subtype}", score=0.9, kind="safety_speech", category=subtype,
                     categories=[subtype, "sensitive"], severity=severity)


def _cats(dets):
    return aggregate_segments([(0.0, 5.0, d) for d in dets], 6.0, 6.0, min_support=2)[0].categories


def test_semantic_high_marks_window_alone():
    assert "sensitive" in _cats([_sem("disaster", "high")])


def test_semantic_medium_needs_corroboration():
    assert "sensitive" not in _cats([_sem("war", "medium")])
    # one sensitive lexicon medium word ("injured") + semantic medium = two distinct hits
    assert "sensitive" in _cats(safety_detections("two soldiers were injured", "audio", 0.9) + [_sem("war", "medium")])
    # R8f: a war-politics word ("army") + semantic "~war" is war politics only (blocked by strict, not standard)
    cats = _cats(safety_detections("the army reports losses", "audio", 0.9) + [_sem("war", "medium")])
    assert "sensitive" not in cats and "war" in cats


def test_env_switch(monkeypatch):
    monkeypatch.setenv("ADVIDEO_SEMANTIC_SAFETY", "0")
    assert not enabled_by_env()
    monkeypatch.setenv("ADVIDEO_SEMANTIC_SAFETY", "1")
    assert enabled_by_env()


def test_semantic_hit_dataclass():
    assert SemanticHit(0.3, "war", "high").severity == "high"


_has_model = importlib.util.find_spec("transformers") is not None and os.path.isdir(
    os.path.expanduser("~/.cache/huggingface/hub/models--sentence-transformers--paraphrase-multilingual-MiniLM-L12-v2"))


@pytest.mark.skipif(not _has_model, reason="transformers or the cached embedding model not available")
def test_real_model_paraphrases_and_everyday_speech():
    from advideo_pipeline.semantic_safety import SemanticSafety

    sem = SemanticSafety()
    hits = sem.classify([
        "Rescuers are digging through the rubble looking for survivors after the quake.",
        "Záchranári sa ho snažili oživiť, no zraneniam podľahol.",
        "This blender will blow your mind, smoothies in ten seconds.",
        "Killer instinct, dominating in every aspect of the game.",
        "ok",
    ])
    assert hits[0].severity is not None and hits[1].severity is not None
    assert hits[2].severity is None and hits[3].severity is None
    assert hits[4].severity is None  # too short


# --------------------------------------------------------------------------- R8e story chunks (no model download)

from advideo_pipeline.semantic_safety import SemanticSafety, build_chunks  # noqa: E402


def test_build_chunks_slides_and_dedupes():
    segs = [(0, 4, "a"), (5, 9, "b"), (10, 14, "c"), (30, 33, "d"), (31, 32, "  ")]
    ch = build_chunks(segs, span_sec=12, stride_sec=6)
    assert [(c.start, c.end, c.text) for c in ch] == [(0, 14, "a b c"), (10, 14, "c"), (30, 33, "d")]
    assert build_chunks([], 24, 8) == [] and build_chunks(segs, 0, 8) == []


def _stub(unit: str, scores: dict[str, float]) -> SemanticSafety:
    s = object.__new__(SemanticSafety)  # skip model loading
    s.high, s.medium, s.unit, s.chunk_span, s.chunk_stride = 0.32, 0.18, unit, 24.0, 8.0
    s.classify = lambda texts: [SemanticHit(scores.get(t, 0.0), "violence", "high" if scores.get(t, 0.0) >= 0.32
                                            else "medium" if scores.get(t, 0.0) >= 0.18 else None) for t in texts]
    return s


def test_chunk_hit_spans_the_chunk():
    segs = [(0, 5, "one"), (6, 11, "two"), (40, 45, "three")]
    s = _stub("chunk", {"one two": 0.4})
    dets = s.track_detections(segs)
    assert [(a, b, d.severity, d.term) for a, b, d in dets] == [(0.0, 11.0, "high", "speech: ~violence")]
    # high chunk hit marks every window it covers
    assert [("sensitive" in w.categories) for w in aggregate_segments(dets, 48)] == [True, True] + [False] * 6


def test_segment_unit_keeps_r8c_behaviour():
    segs = [(0, 5, "one"), (6, 11, "two")]
    dets = _stub("segment", {"two": 0.2}).track_detections(segs)
    assert [(a, b, d.severity) for a, b, d in dets] == [(6, 11, "medium")]


def test_config_defaults_to_chunks():
    from advideo_pipeline.taxonomy import SEMANTIC_SAFETY_CONFIG as cfg

    assert cfg["unit"] == "chunk" and cfg["chunk"]["spanSec"] > cfg["chunk"]["strideSec"] > 0
    assert cfg["thresholds"]["high"] > cfg["thresholds"]["medium"]


def test_whisper_uses_track_detections(monkeypatch):
    """WhisperAudioAnalyzer hands the whole transcript to the classifier (chunks), no per-segment call."""
    import types

    from advideo_pipeline.analyzers import whisper_audio

    seg = lambda a, b, t: types.SimpleNamespace(start=a, end=b, text=t, avg_logprob=-0.1)  # noqa: E731
    calls = {}

    class Fake:
        def track_detections(self, segments, confidences=None):
            calls["n"] = len(segments)
            return [(0.0, 11.0, _sem("violence", "high"))]

    a = object.__new__(whisper_audio.WhisperAudioAnalyzer)
    a._model = types.SimpleNamespace(transcribe=lambda *_a, **_k: ([seg(0, 5, "hello"), seg(6, 11, "world")], None))
    a.language, a._semantic, a._semantic_tried, a._semantic_spec = None, Fake(), True, None
    monkeypatch.setattr("advideo_pipeline.frames.has_audio_stream", lambda _p: True)
    out = a.analyze_track("x.mp4", 12.0)
    assert calls["n"] == 2
    assert any(d.term == "speech: ~violence" and (s, e) == (0.0, 11.0) for s, e, d in out)
