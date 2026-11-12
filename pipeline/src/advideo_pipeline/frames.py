"""Frame sampling.

Two modes behind one function:
- decode=False (default, used by the mock backend): yields timestamp-only frames
  so the pipeline runs with zero heavy dependencies.
- decode=True (used by the YOLO+CLIP backend): decodes real pixels via OpenCV
  and attaches them to each Frame.
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


def has_audio_stream(video_path: str) -> bool | None:
    """True/False via ffprobe; None when ffprobe is unavailable or fails (caller decides)."""
    if not shutil.which("ffprobe"):
        return None
    try:
        out = subprocess.check_output(
            ["ffprobe", "-v", "error", "-select_streams", "a", "-show_entries", "stream=index",
             "-of", "csv=p=0", video_path],
            stderr=subprocess.DEVNULL,
            text=True,
        )
        return bool(out.strip())
    except subprocess.CalledProcessError:
        return None


def sample_frames(
    video_path: str,
    duration_sec: float,
    fps: float = 1.0,
    decode: bool = False,
) -> Iterator[Frame]:
    """Yield frames at `fps` samples per second across the video.

    When decode=True, real pixel data (BGR numpy array, as OpenCV returns it) is
    attached to each Frame; otherwise Frame.image stays None.
    """
    if fps <= 0:
        raise ValueError("fps must be > 0")
    if decode:
        yield from _sample_decoded(video_path, duration_sec, fps)
        return
    step = 1.0 / fps
    t = 0.0
    while t < duration_sec:
        yield Frame(time_sec=round(t, 3), image=None)
        t += step


def _sample_decoded(video_path: str, duration_sec: float, fps: float) -> Iterator[Frame]:
    import cv2  # lazy: only needed for the real backend

    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        raise RuntimeError(f"cannot open video: {video_path}")
    try:
        step = 1.0 / fps
        t = 0.0
        while t < duration_sec:
            cap.set(cv2.CAP_PROP_POS_MSEC, t * 1000.0)
            ok, frame = cap.read()
            if not ok:
                break
            yield Frame(time_sec=round(t, 3), image=frame)
            t += step
    finally:
        cap.release()


def duration_via_cv2(video_path: str) -> float | None:
    """Fallback duration from OpenCV (frame count / fps)."""
    try:
        import cv2
    except ImportError:
        return None
    cap = cv2.VideoCapture(video_path)
    if not cap.isOpened():
        return None
    try:
        fps = cap.get(cv2.CAP_PROP_FPS) or 0
        frames = cap.get(cv2.CAP_PROP_FRAME_COUNT) or 0
        return frames / fps if fps > 0 and frames > 0 else None
    finally:
        cap.release()
