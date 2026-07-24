from .base import Analyzer, TrackAnalyzer
from .mock import MockAnalyzer

__all__ = ["Analyzer", "TrackAnalyzer", "MockAnalyzer",
           "get_analyzer", "get_track_analyzer"]


def get_analyzer(name: str = "mock", **kwargs) -> Analyzer:
    """Factory for frame-level analyzers.

    - "mock"      : deterministic, no dependencies (default)
    - "yolo+clip" : real YOLO objects + CLIP zero-shot labels (needs .[ml])
    - "ocr"       : on-screen text -> brand entities (needs .[ml])
    """
    if name == "mock":
        return MockAnalyzer(**kwargs)
    if name in ("yolo+clip", "yoloclip", "real"):
        from .yolo_clip import YoloClipAnalyzer  # lazy

        return YoloClipAnalyzer(**kwargs)
    if name == "ocr":
        from .ocr import OcrBrandAnalyzer  # lazy

        return OcrBrandAnalyzer(**kwargs)
    raise ValueError(f"unknown frame analyzer backend: {name!r}")


def get_track_analyzer(name: str, **kwargs) -> TrackAnalyzer:
    """Factory for track-level (whole-video) analyzers.

    - "whisper" : spoken keywords & brands from the audio track (needs .[ml])
    """
    if name in ("whisper", "audio"):
        from .whisper_audio import WhisperAudioAnalyzer  # lazy

        return WhisperAudioAnalyzer(**kwargs)
    raise ValueError(f"unknown track analyzer backend: {name!r}")
