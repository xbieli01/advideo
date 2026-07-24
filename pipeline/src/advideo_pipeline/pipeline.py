"""Top-level pipeline: video path -> VideoMap.

Runs any number of frame-level analyzers (per sampled frame) and track-level
analyzers (once over the whole video), merges their timed detections, and
aggregates into content-map segments.
"""

from __future__ import annotations

from datetime import datetime, timezone

from .aggregate import TimedDetection, aggregate_segments
from .analyzers import Analyzer, TrackAnalyzer, get_analyzer, get_track_analyzer
from .frames import duration_via_cv2, probe_duration, sample_frames
from .models import VideoMap

FrameSpec = str | Analyzer
TrackSpec = str | TrackAnalyzer


def analyze_video(
    video_path: str,
    video_id: str | None = None,
    *,
    analyzer: FrameSpec | list[FrameSpec] | None = "mock",
    track: TrackSpec | list[TrackSpec] | None = None,
    fps: float = 1.0,
    window_sec: float = 6.0,
    duration_sec: float | None = None,
) -> VideoMap:
    """Analyze a video and return its content map.

    `analyzer` : frame backend(s) — name, instance, list, or None.
    `track`    : track backend(s) — name, instance, list, or None (e.g. whisper).
    `duration_sec` may be passed explicitly (tests / undecodable files);
    otherwise probed via ffprobe, then OpenCV, with a safe fallback.
    """
    frame_analyzers = [
        a if isinstance(a, Analyzer) else get_analyzer(a)
        for a in _as_list(analyzer)
    ]
    track_analyzers = [
        t if isinstance(t, TrackAnalyzer) else get_track_analyzer(t)
        for t in _as_list(track)
    ]

    needs_frames = any(a.needs_frames for a in frame_analyzers)

    if duration_sec is None:
        duration_sec = probe_duration(video_path)
        if duration_sec is None and (needs_frames or track_analyzers):
            duration_sec = duration_via_cv2(video_path)
        duration_sec = duration_sec or 12.0

    timed: list[TimedDetection] = []

    if frame_analyzers:
        for frame in sample_frames(video_path, duration_sec, fps=fps, decode=needs_frames):
            for a in frame_analyzers:
                for det in a.analyze(frame):
                    timed.append((frame.time_sec, frame.time_sec, det))

    for ta in track_analyzers:
        timed.extend(ta.analyze_track(video_path, duration_sec))

    segments = aggregate_segments(timed, duration_sec, window_sec=window_sec)

    for backend in (*frame_analyzers, *track_analyzers):
        backend.close()

    model_set = "+".join(b.name for b in (*frame_analyzers, *track_analyzers)) or "none"
    return VideoMap(
        video_id=video_id or _default_id(video_path),
        duration_sec=duration_sec,
        generated_at=datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
        segments=segments,
        pipeline={"modelSet": model_set, "fps": fps},
    )


def _as_list(spec) -> list:
    if spec is None:
        return []
    if isinstance(spec, (list, tuple)):
        return list(spec)
    return [spec]


def _default_id(video_path: str) -> str:
    import os

    return os.path.splitext(os.path.basename(video_path))[0] or "video"
