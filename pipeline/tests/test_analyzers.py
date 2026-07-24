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


def test_factory_real_alias_is_registered():
    """The real backend must be wired even if its deps aren't installed here.
    Without torch we expect an ImportError (deps missing), NOT a ValueError
    (unknown backend) — that proves the name is registered."""
    if _HAS_TORCH:
        pytest.skip("torch installed; real backend would actually load")
    with pytest.raises((ImportError, ModuleNotFoundError)):
        get_analyzer("yolo+clip")


def test_taxonomy_categories():
    assert "automotive" in categories_for("car")
    assert "sport" in categories_for("running")
    assert "footwear" in categories_for("sneakers")
    assert categories_for("totally-unknown-term") == []


def test_clip_labels_have_valid_kinds():
    valid = {"action", "scene"}
    assert all(kind in valid for _, kind in CLIP_SCENE_LABELS)


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
