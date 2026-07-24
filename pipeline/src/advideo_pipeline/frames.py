"""Frame sampling.

Real backend uses ffmpeg/OpenCV to decode and sample keyframes. To keep the
mock pipeline dependency-free and testable, we expose a `sample_frames` that
falls back to synthetic timestamps when no decoder/real video is available.
"""

from __future__ import annotations

import shutil
import subprocess
from collections.abc import Iterator

from .models import Frame


def probe_duration(video_path: str) -> float | None:
    """Return duration in seconds via ffprobe, or None if unavailable."""
    if not shutil.which("ffprobe"):
        return None
    try:
        out = subprocess.check_output(
            [
                "ffprobe", "-v", "error", "-show_entries", "format=duration",
                "-of", "default=noprint_wrappers=1:nokey=1", video_path,
            ],
            stderr=subprocess.DEVNULL,
            text=True,
        )
        return float(out.strip())
    except (subprocess.CalledProcessError, ValueError):
        return None


def sample_frames(
    video_path: str,
    duration_sec: float,
    fps: float = 1.0,
) -> Iterator[Frame]:
    """Yield frames at `fps` samples per second across the video.

    In this v1 scaffold we yield timestamp-only frames (image=None). A real
    backend swaps in decoded pixel data here without changing the interface.
    """
    if fps <= 0:
        raise ValueError("fps must be > 0")
    step = 1.0 / fps
    t = 0.0
    while t < duration_sec:
        yield Frame(time_sec=round(t, 3), image=None)
        t += step
