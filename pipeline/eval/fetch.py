"""Download the eval clips from Wikimedia Commons and normalize them to 720p H.264 MP4.

Usage: python eval/fetch.py [--out eval/videos]
Clips (title, licence, author) are listed in eval/samples.json; videos are gitignored.
"""

from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request
from pathlib import Path

HERE = Path(__file__).resolve().parent
API = "https://commons.wikimedia.org/w/api.php"
UA = "advideo-eval/0.1 (https://github.com/xbieli01/advideo)"


def _get(url: str) -> bytes:
    req = urllib.request.Request(url, headers={"User-Agent": UA})
    with urllib.request.urlopen(req, timeout=120) as r:
        return r.read()


def file_info(title: str) -> dict:
    q = urllib.parse.urlencode({
        "action": "query", "format": "json", "titles": title, "prop": "imageinfo",
        "iiprop": "url|size|extmetadata", "iiextmetadatafilter": "LicenseShortName|Artist",
    })
    pages = json.loads(_get(f"{API}?{q}"))["query"]["pages"]
    page = next(iter(pages.values()))
    if "imageinfo" not in page:
        raise RuntimeError(f"not found on Commons: {title}")
    return page["imageinfo"][0]


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=str(HERE / "videos"))
    ap.add_argument("--height", type=int, default=720)
    args = ap.parse_args(argv)
    if not shutil.which("ffmpeg"):
        print("ffmpeg is required", file=sys.stderr)
        return 2
    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    samples = json.loads((HERE / "samples.json").read_text(encoding="utf-8"))["samples"]
    for s in samples:
        dst = out / f"{s['id']}.mp4"
        if dst.exists():
            print(f"skip {dst.name} (exists)")
            continue
        info = file_info(s["commonsTitle"])
        print(f"get  {s['id']:<14} {info['size'] / 1e6:6.1f} MB  {s['commonsTitle']}")
        with tempfile.TemporaryDirectory() as td:
            src = Path(td) / ("src" + Path(info["url"]).suffix)
            src.write_bytes(_get(info["url"]))
            cmd = ["ffmpeg", "-loglevel", "error", "-y", "-i", str(src)]
            if s.get("maxSec"):
                cmd += ["-t", str(s["maxSec"])]
            cmd += ["-vf", f"scale=-2:'min({args.height},ih)'", "-c:v", "libx264", "-preset", "veryfast",
                    "-crf", "23", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "96k", "-movflags", "+faststart", str(dst)]
            subprocess.run(cmd, check=True)
    print(f"done -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
