"""End-to-end test of the mock pipeline + schema validation."""

import json
import os

import jsonschema
import pytest

from advideo_pipeline import analyze_video

SCHEMA_PATH = os.path.join(
    os.path.dirname(__file__), "..", "..", "schema", "video-map.schema.json"
)


@pytest.fixture(scope="module")
def schema():
    with open(SCHEMA_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def test_map_is_schema_valid(schema):
    vmap = analyze_video("fake.mp4", duration_sec=12.0, fps=1.0, window_sec=6.0)
    jsonschema.validate(vmap.to_json(), schema)


def test_segments_cover_duration_without_gaps():
    vmap = analyze_video("fake.mp4", duration_sec=12.0, fps=1.0, window_sec=6.0)
    segs = vmap.to_json()["segments"]
    assert segs[0]["startSec"] == 0.0
    assert segs[-1]["endSec"] == pytest.approx(12.0)
    for a, b in zip(segs, segs[1:]):
        assert a["endSec"] == pytest.approx(b["startSec"])  # contiguous


def test_automotive_then_sport_story():
    """The mock storyline must yield a Tesla/automotive segment then a sport one
    — this is the matching example (Tesla/VW vs Nike) the product is built on."""
    vmap = analyze_video("fake.mp4", duration_sec=12.0, fps=1.0, window_sec=6.0)
    segs = vmap.to_json()["segments"]

    first_cats = set(segs[0]["categories"])
    assert "automotive" in first_cats
    assert any(e["label"] == "Tesla" for e in segs[0]["entities"])

    second_cats = set(segs[1]["categories"])
    assert "sport" in second_cats


def test_deterministic():
    a = analyze_video("fake.mp4", duration_sec=12.0).to_json()
    b = analyze_video("fake.mp4", duration_sec=12.0).to_json()
    a["generatedAt"] = b["generatedAt"] = "x"  # only the timestamp differs
    assert a == b
