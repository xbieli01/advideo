from .base import Analyzer
from .mock import MockAnalyzer

__all__ = ["Analyzer", "MockAnalyzer", "get_analyzer"]


def get_analyzer(name: str = "mock", **kwargs) -> Analyzer:
    """Factory so the CLI/config can select a backend by name.

    Real backends register here later, e.g. name == "yolo+clip".
    """
    if name == "mock":
        return MockAnalyzer(**kwargs)
    raise ValueError(f"unknown analyzer backend: {name!r}")
