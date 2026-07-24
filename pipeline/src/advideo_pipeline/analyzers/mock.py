"""Deterministic mock analyzer — no GPU, no model downloads.

It fabricates a plausible storyline so the end-to-end pipeline and the
downstream matching example are demonstrable:

    a car/driving scene (matches automotive advertisers: Tesla, VW)
    then a running/sport scene (matches sport advertisers: Nike)

Deterministic (seeded by frame time) so tests are stable.
"""

from __future__ import annotations

from ..models import Detection, Frame
from .base import Analyzer

# time-band -> canned detections. Real backends produce these from pixels/audio.
_SCENES: list[tuple[float, float, list[Detection]]] = [
    (
        0.0, 6.0,
        [
            Detection("car", 0.94, kind="object", category="automotive"),
            Detection("road", 0.88, kind="object", category="automotive"),
            Detection("driving", 0.72, kind="action", category="automotive"),
            Detection("Tesla", 0.81, kind="text",
                      entity_type="brand", entity_label="Tesla", category="automotive"),
        ],
    ),
    (
        6.0, 12.0,
        [
            Detection("running", 0.90, kind="action", category="sport"),
            Detection("person", 0.85, kind="object"),
            Detection("sneakers", 0.68, kind="object", category="footwear"),
            Detection("outdoor", 0.60, kind="scene"),
        ],
    ),
]


class MockAnalyzer(Analyzer):
    name = "mock-0"

    def analyze(self, frame: Frame) -> list[Detection]:
        for start, end, dets in _SCENES:
            if start <= frame.time_sec < end:
                return list(dets)
        return [Detection("unknown", 0.30, kind="scene")]
