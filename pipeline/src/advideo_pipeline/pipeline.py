"""Top-level pipeline: video path -> VideoMap."""

from __future__ import annotations

from datetime import datetime, timezone

from .aggregate import aggregate_segments
from .analyzers import Analyzer, get_analyzer
from .frames import probe_duration, sample_frames
from .models import VideoMap


def analyze_video(
    video_path: str,
    video_id: str | None = None,
    *,
    analyzer: Analyzer | str = "mock",
    fps: float = 1.0,
    window_sec: float = 6.0,
    duration_sec: float | None = None,
) -> VideoMap:
    """Analyze a video and return its content map.

    `duration_sec` can be passed explicitly (useful for tests / when the file
    isn't decodable in this environment); otherwise we probe via ffprobe and
    fall back to a default so the scaffold always produces output.
    """
    if duration_sec is None:
        duration_sec = probe_duration(video_path) or 12.0

    backend: Analyzer = (
        analyzer if isinstance(analyzer, Analyzer) else get_analyzer(analyzer)
    )

    frame_dets = [
        (frame, backend.analyze(frame))
        for frame in sample_frames(video_path, duration_sec, fps=fps)
    ]
    segments = aggregate_segments(frame_dets, duration_sec, window_sec=window_sec)
    backend.close()

    return VideoMap(
        video_id=video_id or _default_id(video_path),
        duration_sec=duration_sec,
        generated_at=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        segments=segments,
        pipeline={"modelSet": backend.name, "fps": fps},
    )


def _default_id(video_path: str) -> str:
    import os

    return os.path.splitext(os.path.basename(video_path))[0] or "video"
