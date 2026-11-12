"""Core data types shared across the pipeline."""

from __future__ import annotations

from dataclasses import dataclass, field
from typing import Any


@dataclass(frozen=True)
class Frame:
    """A sampled frame at a given time position."""

    time_sec: float
    # Real backends carry pixel data here (e.g. numpy array); the mock backend
    # only needs the timestamp, so `image` stays None in tests.
    image: Any = None


@dataclass
class Detection:
    """One thing an analyzer found in a frame."""

    term: str
    score: float
    kind: str = "keyword"  # keyword | object | action | scene | text | audio
    category: str | None = None
    entity_type: str | None = None  # brand | product | person | place | org
    entity_label: str | None = None
    # all ad categories of the term (``category`` is the first one, kept for compatibility)
    categories: list[str] = field(default_factory=list)


@dataclass
class Segment:
    start_sec: float
    end_sec: float
    keywords: list[dict] = field(default_factory=list)
    entities: list[dict] = field(default_factory=list)
    categories: list[str] = field(default_factory=list)
    signals: dict = field(default_factory=dict)

    def to_json(self) -> dict:
        return {
            "startSec": round(self.start_sec, 3),
            "endSec": round(self.end_sec, 3),
            "keywords": self.keywords,
            "entities": self.entities,
            "categories": self.categories,
            "signals": self.signals,
        }


@dataclass
class VideoMap:
    video_id: str
    duration_sec: float
    generated_at: str
    segments: list[Segment]
    pipeline: dict = field(default_factory=dict)
    schema_version: str = "1.0"

    def to_json(self) -> dict:
        return {
            "schemaVersion": self.schema_version,
            "videoId": self.video_id,
            "durationSec": round(self.duration_sec, 3),
            "generatedAt": self.generated_at,
            "pipeline": self.pipeline,
            "segments": [s.to_json() for s in self.segments],
        }
