"""Analyzer interface — the seam between the pipeline and the ML models.

A real backend (YOLO for objects, CLIP for zero-shot labels/scene, an OCR model
for on-screen text/brands, Whisper for audio keywords) implements `analyze` and
returns `Detection`s for a frame. The rest of the pipeline never imports torch.
"""

from __future__ import annotations

from abc import ABC, abstractmethod

from ..models import Detection, Frame


class Analyzer(ABC):
    name: str = "analyzer"
    # Whether the backend needs decoded pixel data. The pipeline reads this to
    # decide if frames must be decoded (real backends) or can be timestamp-only
    # (mock). Keeps torch/opencv out of the default path.
    needs_frames: bool = False

    @abstractmethod
    def analyze(self, frame: Frame) -> list[Detection]:
        """Return detections for a single frame."""
        raise NotImplementedError

    def close(self) -> None:
        """Release models / GPU memory. Optional."""
        return None


class TrackAnalyzer(ABC):
    """Whole-video (track-level) analyzer — e.g. audio/Whisper.

    Unlike `Analyzer` it does not work per sampled frame; it processes the video
    once and returns time-ranged detections `(start_sec, end_sec, Detection)`
    that the pipeline distributes across the segments they overlap.
    """

    name: str = "track"

    @abstractmethod
    def analyze_track(
        self, video_path: str, duration_sec: float
    ) -> list[tuple[float, float, Detection]]:
        raise NotImplementedError

    def close(self) -> None:
        return None
