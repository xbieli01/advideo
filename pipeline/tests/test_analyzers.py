"""Tests for the analyzer layer that don't require heavy ML deps."""

import importlib.util

import pytest

from advideo_pipeline.analyzers import Analyzer, MockAnalyzer, get_analyzer
from advideo_pipeline.models import Frame
from advideo_pipeline.taxonomy import CLIP_SCENE_LABELS, categories_for

_HAS_TORCH = importlib.util.find_spec("torch") is not None


def test_factory_mock():
    a = get_analyzer("mock")
    assert isinstance(a, MockAnalyzer)
    assert a.needs_frames is False


def test_factory_unknown_raises():
    with pytest.raises(ValueError):
        get_analyzer("does-not-exist")


@pytest.mark.parametrize("name", ["yolo+clip", "ocr"])
def test_frame_backends_registered(name):
    """Real frame backends must be wired even if deps aren't installed here.
    Without their deps we expect an ImportError (deps missing), NOT a ValueError
    (unknown backend) — that proves the name is registered."""
    if _HAS_TORCH:
        pytest.skip("ml deps installed; backend would actually load")
    with pytest.raises((ImportError, ModuleNotFoundError)):
        get_analyzer(name)


def test_track_backend_registered():
    from advideo_pipeline.analyzers import get_track_analyzer

    if importlib.util.find_spec("faster_whisper") is not None:
        pytest.skip("faster-whisper installed; backend would actually load")
    with pytest.raises((ImportError, ModuleNotFoundError)):
        get_track_analyzer("whisper")


def test_taxonomy_categories():
    assert "automotive" in categories_for("car")
    assert "sport" in categories_for("running")
    assert "footwear" in categories_for("sneakers")
    assert categories_for("totally-unknown-term") == []


def test_clip_labels_have_valid_kinds():
    valid = {"action", "scene", "object"}
    assert all(kind in valid for _, kind in CLIP_SCENE_LABELS)


def test_every_clip_group_has_neutral_distractors():
    # CLIP is scored with a softmax per kind; without neutral labels any frame is forced into a real label
    from advideo_pipeline.taxonomy import CLIP_NEUTRAL_LABELS

    kinds = {kind for _, kind in CLIP_SCENE_LABELS}
    for kind in kinds:
        labels = [lbl for lbl, k in CLIP_SCENE_LABELS if k == kind]
        assert any(lbl in CLIP_NEUTRAL_LABELS for lbl in labels), kind
        assert all(categories_for(lbl) == [] for lbl in labels if lbl in CLIP_NEUTRAL_LABELS), kind


def test_taxonomy_maps_cooking_and_coco_food():
    assert categories_for("cooking") == ["food"]
    for t in ("sandwich", "cake", "bowl", "carrot"):
        assert "food" in categories_for(t)
    assert set(categories_for("running")) == {"sport", "fitness"}


def test_mock_is_analyzer_and_deterministic():
    a = get_analyzer("mock")
    assert isinstance(a, Analyzer)
    f = Frame(time_sec=1.0)
    assert a.analyze(f) == a.analyze(f)


@pytest.mark.skipif(not _HAS_TORCH, reason="ML deps not installed")
def test_real_backend_constructs_when_deps_present():
    a = get_analyzer("yolo+clip")
    assert a.needs_frames is True
    assert a.name == "yolo+clip"
