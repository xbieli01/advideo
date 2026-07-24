"""Whisper audio analyzer — spoken keywords & brands from the audio track.

Track-level analyzer: transcribes the whole video once (faster-whisper, lazy
import) and, for each transcript segment, extracts brand entities and taxonomy
keywords via the pure `textmatch` layer — returning them as time-ranged
detections that the pipeline maps onto the overlapping content-map segments.

Install with `pip install -e .[ml]`. faster-whisper decodes the audio via ffmpeg.
"""

from __future__ import annotations

from ..models import Detection
from ..textmatch import brand_detections, keyword_detections
from .base import TrackAnalyzer


class WhisperAudioAnalyzer(TrackAnalyzer):
    name = "whisper"

    def __init__(
        self,
        model_size: str = "base",
        device: str | None = None,
        compute_type: str = "auto",
        language: str | None = None,
    ) -> None:
        from faster_whisper import WhisperModel  # lazy

        dev = device
        if dev is None:
            try:
                import torch

                dev = "cuda" if torch.cuda.is_available() else "cpu"
            except ImportError:
                dev = "cpu"
        self._model = WhisperModel(model_size, device=dev, compute_type=compute_type)
        self.language = language

    def analyze_track(
        self, video_path: str, duration_sec: float
    ) -> list[tuple[float, float, Detection]]:
        segments, _info = self._model.transcribe(video_path, language=self.language)
        out: list[tuple[float, float, Detection]] = []
        for seg in segments:
            text = seg.text or ""
            start = float(seg.start)
            end = float(seg.end)
            score = _avg_logprob_to_score(getattr(seg, "avg_logprob", None))
            for det in brand_detections(text, kind="audio", score=score):
                out.append((start, end, det))
            for det in keyword_detections(text, kind="audio", score=score):
                out.append((start, end, det))
        return out

    def close(self) -> None:
        self._model = None


def _avg_logprob_to_score(avg_logprob: float | None) -> float:
    """Map Whisper avg_logprob (~[-1, 0]) to a [0.5, 1.0] confidence-ish score."""
    if avg_logprob is None:
        return 0.7
    import math

    return round(min(1.0, max(0.5, math.exp(avg_logprob))), 3)
