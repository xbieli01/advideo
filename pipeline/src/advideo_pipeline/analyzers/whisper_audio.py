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
from ..textmatch import brand_detections, keyword_detections, news_detections, safety_detections
from .base import TrackAnalyzer


log = logging.getLogger(__name__)


class WhisperAudioAnalyzer(TrackAnalyzer):
    name = "whisper"

    def __init__(
        self,
        model_size: str | None = None,
        device: str | None = None,
        compute_type: str = "auto",
        language: str | None = "",
        semantic_safety: "bool | object | None" = None,
    ) -> None:
        """`semantic_safety`: None = on when enabled by env (ADVIDEO_SEMANTIC_SAFETY, default on) and the
        model can be loaded; False = off; or a ready SemanticSafety instance (shared across videos).
        `model_size`: faster-whisper model; None = ADVIDEO_WHISPER_MODEL or "base" (R8d: "small" transcribes
        Slovak news far better, ~3x slower on CPU). `language`: "" = from the language packs
        (ADVIDEO_WHISPER_LANGUAGE, or the only active pack), None = auto-detect, or an explicit code."""
        from faster_whisper import WhisperModel  # lazy

        dev = device
        if dev is None:
            try:
                import torch

                dev = "cuda" if torch.cuda.is_available() else "cpu"
            except ImportError:
                dev = "cpu"
        import os

        self.model_size = model_size or os.environ.get("ADVIDEO_WHISPER_MODEL", "").strip() or "base"
        self._model = WhisperModel(self.model_size, device=dev, compute_type=compute_type)
        if language == "":
            from ..languages import whisper_language

            language = whisper_language()
        self.language = language
        self._semantic_spec = semantic_safety
        self._semantic = semantic_safety if semantic_safety not in (None, True, False) else None
        self._semantic_tried = self._semantic is not None or semantic_safety is False

    def _semantic_classifier(self):
        if not self._semantic_tried:
            self._semantic_tried = True
            from ..semantic_safety import SemanticSafety, load_or_none

            self._semantic = SemanticSafety() if self._semantic_spec is True else load_or_none()
        return self._semantic

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
        semantic = self._semantic_classifier()
        if semantic and segments:
            confs = [_avg_logprob_to_score(getattr(seg, "avg_logprob", None)) for seg in segments]
            timed = [(float(seg.start), float(seg.end), seg.text or "") for seg in segments]
            if hasattr(semantic, "track_detections"):
                out.extend(semantic.track_detections(timed, confs))  # R8e: story chunks (or segments)
            else:  # duck-typed classifiers with only detections(): per segment
                for (a, b, _), ds in zip(timed, semantic.detections([t for _, _, t in timed], confs)):
                    out.extend((a, b, d) for d in ds)
        for seg in segments:
            text = seg.text or ""
            start = float(seg.start)
            end = float(seg.end)
            score = _avg_logprob_to_score(getattr(seg, "avg_logprob", None))
            for det in brand_detections(text, kind="audio", score=score):
                out.append((start, end, det))
            for det in keyword_detections(text, kind="audio", score=score):
                out.append((start, end, det))
            # brand safety from speech (severity rules applied per window in aggregate) + news cues
            for det in safety_detections(text, kind="audio", score=score):
                out.append((start, end, det))
            for det in news_detections(text, kind="audio", score=score):
                out.append((start, end, det))
        return out

    def close(self) -> None:
        self._model = None
        if self._semantic is not None and self._semantic_spec in (None, True):
            self._semantic.close()  # created here; a passed-in instance belongs to the caller
        self._semantic = None


def _avg_logprob_to_score(avg_logprob: float | None) -> float:
    """Map Whisper avg_logprob (~[-1, 0]) to a [0.5, 1.0] confidence-ish score."""
    if avg_logprob is None:
        return 0.7
    import math

    return round(min(1.0, max(0.5, math.exp(avg_logprob))), 3)
