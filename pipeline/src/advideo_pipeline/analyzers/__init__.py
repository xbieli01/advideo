from .base import Analyzer
from .mock import MockAnalyzer

__all__ = ["Analyzer", "MockAnalyzer", "get_analyzer"]


def get_analyzer(name: str = "mock", **kwargs) -> Analyzer:
    """Factory so the CLI/config can select a backend by name.

    - "mock"      : deterministic, no dependencies (default)
    - "yolo+clip" : real YOLO objects + CLIP zero-shot labels (needs .[ml] deps)
    """
    if name == "mock":
        return MockAnalyzer(**kwargs)
    if name in ("yolo+clip", "yoloclip", "real"):
        from .yolo_clip import YoloClipAnalyzer  # lazy: pulls torch only on demand

        return YoloClipAnalyzer(**kwargs)
    raise ValueError(f"unknown analyzer backend: {name!r}")
