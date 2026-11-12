"""Whisper audio analyzer — spoken keywords & brands from the audio track.

Track-level analyzer: transcribes the whole video once (faster-whisper, lazy
import) and, for each transcript segment, extracts brand entities and taxonomy
keywords via the pure `textmatch` layer — returning them as time-ranged
detections that the pipeline maps onto the overlapping content-map segments.

Install with `pip install -e .[ml]`. faster-whisper decodes the audio via ffmpeg.
"""

from __future__ import annotations

import logging

from ..models import Detection
from ..textmatch import brand_detections, keyword_detections
from .base import TrackAnalyzer


log = logging.getLogger(__name__)


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
        from ..frames import has_audio_stream

        if has_audio_stream(video_path) is False:
            log.info("whisper: %s has no audio track, skipped", video_path)
            return []
        out: list[tuple[float, float, Detection]] = []
        try:
            segments, _info = self._model.transcribe(video_path, language=self.language)
            segments = list(segments)  # decoding is lazy; surface errors here
        except (IndexError, ValueError, RuntimeError, OSError) as exc:
            # unreadable/odd audio must not fail the whole video analysis; visual backends still count
            log.warning("whisper: audio of %s could not be decoded (%s), skipped", video_path, exc)
            return []
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
