"""Track-analyzer integration into the pipeline, using a fake TrackAnalyzer.

Proves the pipeline merges track-level (whole-video, time-ranged) detections
into the right segments — without needing Whisper/OCR models.
"""

import json
import os

import jsonschema
import pytest

from advideo_pipeline import analyze_video
from advideo_pipeline.analyzers import TrackAnalyzer, get_track_analyzer
from advideo_pipeline.models import Detection

SCHEMA_PATH = os.path.join(
    os.path.dirname(__file__), "..", "..", "schema", "video-map.schema.json"
)


class FakeAudio(TrackAnalyzer):
    name = "fake-audio"

    def analyze_track(self, video_path, duration_sec):
        # a spoken Nike mention in the second window only (6..12s)
        return [
            (7.0, 9.0, Detection(
                term="Nike", score=0.9, kind="audio",
                category="sport", entity_type="brand", entity_label="Nike",
            ))
        ]


@pytest.fixture(scope="module")
def schema():
    with open(SCHEMA_PATH, encoding="utf-8") as fh:
        return json.load(fh)


def test_track_detection_lands_in_right_segment(schema):
    vmap = analyze_video(
        "x.mp4", duration_sec=12.0, analyzer="mock", track=FakeAudio(),
        window_sec=6.0, fps=1.0,
    )
    jsonschema.validate(vmap.to_json(), schema)
    segs = vmap.to_json()["segments"]

    # segment 0 (0..6): no Nike audio
    assert not any(e["label"] == "Nike" for e in segs[0]["entities"])
    # segment 1 (6..12): Nike brand from audio present
    assert any(e["label"] == "Nike" for e in segs[1]["entities"])
    assert "Nike" in segs[1]["signals"].get("audioKeywords", [])


def test_modelset_combines_frame_and_track():
    vmap = analyze_video("x.mp4", duration_sec=12.0, analyzer="mock", track=FakeAudio())
    assert vmap.to_json()["pipeline"]["modelSet"] == "mock-0+fake-audio"


def test_track_only_no_frame_analyzer():
    vmap = analyze_video("x.mp4", duration_sec=12.0, analyzer=None, track=FakeAudio())
    assert vmap.to_json()["pipeline"]["modelSet"] == "fake-audio"


def test_track_factory_unknown_raises():
    with pytest.raises(ValueError):
        get_track_analyzer("nope")
