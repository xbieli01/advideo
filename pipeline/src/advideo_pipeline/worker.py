"""R4: long-running analysis worker — models stay loaded between videos.

``advideo-analyze`` (one process per video) spends ~35 s on imports and model loading on a 4-core CPU before the
first frame — more than it then needs for a 30 s clip. ``advideo-worker`` loads each backend once and serves jobs
over a JSON-lines protocol on stdin/stdout (the orchestrator keeps a small pool of these processes,
``Pipeline:Mode=worker``):

request  ``{"id": "j1", "videoUri": "...", "videoId": "v1", "analyzer": "yolo+clip,ocr", "track": "whisper",
            "languages": "sk,en", "fps": 1.0, "windowSec": 6.0}``  (only ``id`` and ``videoUri`` are required)
         ``{"id": "p", "op": "ping"}`` -> ``{"id": "p", "ok": true, "op": "pong"}``
response ``{"id": "j1", "ok": true, "map": {...content map...}, "processingSec": 12.3}``
         ``{"id": "j1", "ok": false, "transient": false, "exitCode": 3, "error": "SOURCE_ERROR permanent: ..."}``

``exitCode``/``transient`` mean the same as the CLI exit codes (3 permanent source error, 4 transient source error,
1 unexpected). Everything else the libraries print goes to stderr, so stdout carries only protocol lines.
Language-independent backends (yolo+clip, clip-brand) are shared by all jobs; OCR readers and semantic-safety
prototypes are cached per language set; Whisper's model is shared and only its language is switched per job.
"""

from __future__ import annotations

import json
import sys
import time
import traceback
from collections import OrderedDict
from typing import Any, TextIO

from . import languages
from .analyzers import get_analyzer, get_track_analyzer
from .cli import EXIT_FAILED, EXIT_SOURCE_PERMANENT, EXIT_SOURCE_TRANSIENT
from .pipeline import analyze_video
from .sources import SourceError, default_video_id, materialize

_LANGUAGE_AWARE = {"ocr"}
_MAX_LANGUAGE_SETS = 3


class ModelCache:
    """Keeps analyzer instances across jobs (LRU per language set for language-aware backends)."""

    def __init__(self) -> None:
        self._frame: dict[str, Any] = {}
        self._by_lang: OrderedDict[tuple[str, tuple[str, ...]], Any] = OrderedDict()
        self._track: dict[str, Any] = {}
        self._semantic: OrderedDict[tuple[str, ...], Any] = OrderedDict()

    def frame(self, name: str, langs: tuple[str, ...]):
        if name not in _LANGUAGE_AWARE:
            if name not in self._frame:
                self._frame[name] = get_analyzer(name)
            return self._frame[name]
        key = (name, langs)
        if key not in self._by_lang:
            self._by_lang[key] = get_analyzer(name)
            while len(self._by_lang) > _MAX_LANGUAGE_SETS:
                _, old = self._by_lang.popitem(last=False)
                old.close()
        self._by_lang.move_to_end(key)
        return self._by_lang[key]

    def track(self, name: str, langs: tuple[str, ...]):
        if name not in self._track:
            # semantic classifier is attached per job (it depends on the language set)
            self._track[name] = get_track_analyzer(name, semantic_safety=False) if name in ("whisper", "audio") \
                else get_track_analyzer(name)
        inst = self._track[name]
        if name in ("whisper", "audio"):
            inst.language = languages.whisper_language()
            inst._semantic = self._semantic_for(langs)
            inst._semantic_tried = True
        return inst

    def _semantic_for(self, langs: tuple[str, ...]):
        from .semantic_safety import load_or_none

        if langs not in self._semantic:
            self._semantic[langs] = load_or_none()
            while len(self._semantic) > _MAX_LANGUAGE_SETS:
                _, old = self._semantic.popitem(last=False)
                if old is not None:
                    old.close()
        self._semantic.move_to_end(langs)
        return self._semantic[langs]


def _split(v: str | None) -> list[str]:
    return [x.strip() for x in (v or "").split(",") if x.strip()]


def handle(job: dict, cache: ModelCache) -> dict:
    jid = job.get("id")
    if job.get("op") == "ping":
        return {"id": jid, "ok": True, "op": "pong"}
    uri = job.get("videoUri")
    if not uri:
        return {"id": jid, "ok": False, "transient": False, "exitCode": EXIT_FAILED, "error": "videoUri is required"}
    try:
        langs = job.get("languages")
        if langs:
            languages.set_languages(languages.available_languages() if langs == "all" else langs)
        else:
            languages.set_languages(None)
        active = languages.active().codes
        frames = [cache.frame(n, active) for n in _split(job.get("analyzer") or "mock")]
        tracks = [cache.track(n, active) for n in _split(job.get("track"))]
        t0 = time.perf_counter()
        with materialize(uri) as local:
            vmap = analyze_video(local, video_id=job.get("videoId") or default_video_id(uri), analyzer=frames,
                                 track=tracks, fps=float(job.get("fps") or 1.0),
                                 window_sec=float(job.get("windowSec") or 6.0))
        return {"id": jid, "ok": True, "map": vmap.to_json(), "processingSec": round(time.perf_counter() - t0, 2)}
    except SourceError as exc:
        kind = "transient" if exc.transient else "permanent"
        return {"id": jid, "ok": False, "transient": exc.transient,
                "exitCode": EXIT_SOURCE_TRANSIENT if exc.transient else EXIT_SOURCE_PERMANENT,
                "error": f"SOURCE_ERROR {kind}: {exc}"}
    except ValueError as exc:  # unknown analyzer / language: bad job, retrying will not help
        return {"id": jid, "ok": False, "transient": False, "exitCode": EXIT_FAILED, "error": str(exc)}
    except Exception as exc:  # noqa: BLE001 - report and keep serving
        traceback.print_exc(file=sys.stderr)
        return {"id": jid, "ok": False, "transient": True, "exitCode": EXIT_FAILED, "error": f"{type(exc).__name__}: {exc}"}
    finally:
        languages.set_languages(None)


def serve(stdin: TextIO, stdout: TextIO, cache: ModelCache | None = None) -> int:
    cache = cache or ModelCache()
    real_stdout = stdout
    for line in stdin:
        line = line.strip()
        if not line:
            continue
        try:
            job = json.loads(line)
            if not isinstance(job, dict):
                raise ValueError("request must be a JSON object")
        except ValueError as exc:
            resp = {"id": None, "ok": False, "transient": False, "exitCode": EXIT_FAILED, "error": f"bad request: {exc}"}
        else:
            saved, sys.stdout = sys.stdout, sys.stderr  # libraries must not write into the protocol stream
            try:
                resp = handle(job, cache)
            finally:
                sys.stdout = saved
        real_stdout.write(json.dumps(resp, ensure_ascii=False) + "\n")
        real_stdout.flush()
    return 0


def main(argv: list[str] | None = None) -> int:
    import argparse

    p = argparse.ArgumentParser(prog="advideo-worker", description=__doc__.split("\n\n")[0])
    p.add_argument("--preload", default="", help="backends to load at start, e.g. 'yolo+clip,ocr,whisper'")
    args = p.parse_args(argv)
    cache = ModelCache()
    if args.preload:
        saved, sys.stdout = sys.stdout, sys.stderr
        try:
            active = languages.active().codes
            for name in _split(args.preload):
                (cache.track if name in ("whisper", "audio") else cache.frame)(name, active)
        finally:
            sys.stdout = saved
    print("advideo-worker ready", file=sys.stderr, flush=True)
    return serve(sys.stdin, sys.stdout, cache)


if __name__ == "__main__":
    raise SystemExit(main())
