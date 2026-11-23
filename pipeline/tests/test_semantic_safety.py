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
    # one lexicon medium word ("army") + semantic medium = two distinct hits
    assert "sensitive" in _cats(safety_detections("the army reports losses", "audio", 0.9) + [_sem("war", "medium")])


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
