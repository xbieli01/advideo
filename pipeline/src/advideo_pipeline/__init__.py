"""advideo offline video analysis pipeline.

Turns a video into a JSON content map (timestamped keywords / entities /
categories) used downstream by the ad-serving matcher and the client snippet.

The pipeline is intentionally split so the heavy ML part is pluggable:

    frames.sample_frames()  ->  Analyzer.analyze(frame)  ->  aggregate_segments()

The default Analyzer is a deterministic MOCK, so the whole pipeline runs and is
testable with no GPU and no model downloads. Real backends (YOLO/CLIP/OCR/ASR)
implement the same `Analyzer` interface and are selected via config.
"""

from .models import Frame, Detection, Segment, VideoMap
from .pipeline import analyze_video

__all__ = ["Frame", "Detection", "Segment", "VideoMap", "analyze_video"]
__version__ = "0.1.0"
