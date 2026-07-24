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

    @abstractmethod
    def analyze(self, frame: Frame) -> list[Detection]:
        """Return detections for a single frame."""
        raise NotImplementedError

    def close(self) -> None:
        """Release models / GPU memory. Optional."""
        return None
