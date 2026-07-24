"""CLI: advideo-analyze --video in.mp4 --out map.json"""

from __future__ import annotations

import argparse
import json
import sys

from .pipeline import analyze_video


def main(argv: list[str] | None = None) -> int:
    p = argparse.ArgumentParser(
        prog="advideo-analyze",
        description="Analyze a video into an advideo JSON content map.",
    )
    p.add_argument("--video", required=True, help="path to the video file")
    p.add_argument("--out", help="output JSON path (default: stdout)")
    p.add_argument("--video-id", help="explicit video id (default: filename)")
    p.add_argument("--analyzer", default="mock",
                   help="frame backend(s), comma-separated: mock | yolo+clip | ocr")
    p.add_argument("--track", default=None,
                   help="track backend(s), comma-separated: whisper")
    p.add_argument("--fps", type=float, default=1.0, help="frame sample rate")
    p.add_argument("--window-sec", type=float, default=6.0, help="segment window length")
    p.add_argument("--duration-sec", type=float, default=None,
                   help="override duration (skip ffprobe)")
    args = p.parse_args(argv)

    def _split(v):
        return [x.strip() for x in v.split(",") if x.strip()] if v else None

    vmap = analyze_video(
        args.video,
        video_id=args.video_id,
        analyzer=_split(args.analyzer),
        track=_split(args.track),
        fps=args.fps,
        window_sec=args.window_sec,
        duration_sec=args.duration_sec,
    )
    payload = json.dumps(vmap.to_json(), ensure_ascii=False, indent=2)

    if args.out:
        with open(args.out, "w", encoding="utf-8") as fh:
            fh.write(payload + "\n")
        print(f"wrote {args.out} ({len(vmap.segments)} segments)", file=sys.stderr)
    else:
        print(payload)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
