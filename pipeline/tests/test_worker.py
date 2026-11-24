"""R4 warm worker: JSON-lines protocol, error classification, stdout hygiene, model reuse."""

from __future__ import annotations

import io
import json

from advideo_pipeline import languages, worker
from advideo_pipeline.analyzers.mock import MockAnalyzer


def _run(lines: list, cache=None) -> list[dict]:
    stdin = io.StringIO("\n".join(json.dumps(x) if not isinstance(x, str) else x for x in lines) + "\n")
    stdout = io.StringIO()
    worker.serve(stdin, stdout, cache)
    return [json.loads(line) for line in stdout.getvalue().splitlines()]


def test_ping_and_mock_job(tmp_path):
    video = tmp_path / "clip.mp4"
    video.write_bytes(b"not decoded by the mock analyzer")
    out = _run([{"id": "p", "op": "ping"}, {"id": "j1", "videoUri": str(video), "analyzer": "mock"}])
    assert out[0] == {"id": "p", "ok": True, "op": "pong"}
    assert out[1]["ok"] and out[1]["id"] == "j1"
    assert out[1]["map"]["videoId"] == "clip" and out[1]["map"]["segments"]


def test_missing_source_is_permanent_and_worker_keeps_serving(tmp_path):
    video = tmp_path / "ok.mp4"
    video.write_bytes(b"x")
    out = _run([{"id": "a", "videoUri": str(tmp_path / "nope.mp4"), "analyzer": "mock"},
                {"id": "b", "videoUri": str(video), "analyzer": "mock"}])
    assert out[0]["ok"] is False and out[0]["exitCode"] == 3 and out[0]["transient"] is False
    assert "SOURCE_ERROR permanent" in out[0]["error"]
    assert out[1]["ok"] is True


def test_bad_requests(tmp_path):
    out = _run(["{not json", {"id": "x"}, {"id": "y", "videoUri": str(tmp_path), "analyzer": "nope"}])
    assert out[0]["ok"] is False and "bad request" in out[0]["error"]
    assert out[1]["error"] == "videoUri is required"
    assert out[2]["ok"] is False and out[2]["transient"] is False


def test_unknown_language_is_not_retried(tmp_path):
    video = tmp_path / "v.mp4"
    video.write_bytes(b"x")
    out = _run([{"id": "l", "videoUri": str(video), "analyzer": "mock", "languages": "xx"}])
    assert out[0]["ok"] is False and out[0]["transient"] is False and "unknown language" in out[0]["error"]
    assert languages.active().codes == tuple(languages.configured_languages())  # reset after the job


class _Chatty(MockAnalyzer):
    name = "chatty"
    created = 0

    def __init__(self):
        super().__init__()
        type(self).created += 1
        print("loading weights...")  # libraries do this; must not corrupt the protocol

    def analyze(self, frame):
        print("frame!")
        return super().analyze(frame)


def test_stdout_carries_only_protocol_and_models_are_reused(tmp_path, monkeypatch):
    monkeypatch.setattr(worker, "get_analyzer", lambda name, **kw: _Chatty() if name == "chatty" else MockAnalyzer())
    video = tmp_path / "v.mp4"
    video.write_bytes(b"x")
    out = _run([{"id": str(i), "videoUri": str(video), "analyzer": "chatty"} for i in range(3)])
    assert [o["ok"] for o in out] == [True, True, True]  # every stdout line parsed as JSON
    assert _Chatty.created == 1


def test_language_aware_backends_are_cached_per_language_set(monkeypatch):
    made = []
    monkeypatch.setattr(worker, "get_analyzer", lambda name, **kw: made.append(name) or MockAnalyzer())
    cache = worker.ModelCache()
    a = cache.frame("ocr", ("sk", "en"))
    assert cache.frame("ocr", ("sk", "en")) is a
    assert cache.frame("ocr", ("hu", "en")) is not a
    cache.frame("yolo+clip", ("sk",))
    cache.frame("yolo+clip", ("hu",))
    assert made == ["ocr", "ocr", "yolo+clip"]
