"""Source resolution: local paths, HTTP downloads, HLS remux, retries and error classification."""

from __future__ import annotations

import functools
import http.server
import json
import os
import shutil
import subprocess
import threading

import pytest

from advideo_pipeline import cli
from advideo_pipeline.sources import (
    PermanentSourceError,
    SourceOptions,
    TransientSourceError,
    default_video_id,
    materialize,
)

needs_ffmpeg = pytest.mark.skipif(not (shutil.which("ffmpeg") and shutil.which("ffprobe")),
                                  reason="ffmpeg/ffprobe not installed")

FAST = SourceOptions(allow_private_hosts=True, backoff_sec=0.01, retries=3, timeout_sec=5)


@pytest.fixture(scope="module")
def media_dir(tmp_path_factory):
    d = tmp_path_factory.mktemp("media")
    if shutil.which("ffmpeg"):
        subprocess.run(["ffmpeg", "-nostdin", "-y", "-loglevel", "error", "-f", "lavfi", "-i",
                        "testsrc=duration=3:size=160x120:rate=10", "-pix_fmt", "yuv420p",
                        str(d / "clip.mp4")], check=True)
        hls = d / "hls"
        hls.mkdir()
        subprocess.run(["ffmpeg", "-nostdin", "-y", "-loglevel", "error", "-i", str(d / "clip.mp4"),
                        "-c", "copy", "-hls_time", "1", "-hls_playlist_type", "vod",
                        str(hls / "index.m3u8")], check=True)
    (d / "page.html").write_text("<html>not a video</html>")
    return d


class _Handler(http.server.SimpleHTTPRequestHandler):
    flaky_left = {"n": 0}

    def log_message(self, *args):  # keep test output quiet
        pass

    def do_GET(self):
        if self.path.startswith("/flaky"):
            if self.flaky_left["n"] > 0:
                self.flaky_left["n"] -= 1
                self.send_error(503)
                return
            self.path = "/clip.mp4"
        if self.path == "/forbidden.mp4":
            self.send_error(403)
            return
        if self.path == "/huge.mp4":
            self.send_response(200)
            self.send_header("Content-Type", "video/mp4")
            self.send_header("Content-Length", str(10 * 1024**3))
            self.end_headers()
            return
        super().do_GET()


@pytest.fixture(scope="module")
def server(media_dir):
    handler = functools.partial(_Handler, directory=str(media_dir))
    httpd = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
    t = threading.Thread(target=httpd.serve_forever, daemon=True)
    t.start()
    yield f"http://127.0.0.1:{httpd.server_address[1]}"
    httpd.shutdown()


def test_local_path_is_used_in_place(tmp_path):
    f = tmp_path / "v.mp4"
    f.write_bytes(b"x")
    with materialize(str(f), FAST) as p:
        assert p == str(f)
    with materialize(f"file://{f}", FAST) as p:
        assert p == str(f)


def test_missing_local_file_is_permanent(tmp_path):
    with pytest.raises(PermanentSourceError):
        with materialize(str(tmp_path / "nope.mp4"), FAST):
            pass


def test_unsupported_scheme_is_permanent():
    with pytest.raises(PermanentSourceError):
        with materialize("ftp://example.com/a.mp4", FAST):
            pass


@needs_ffmpeg
def test_http_download_and_cleanup(server):
    with materialize(f"{server}/clip.mp4", FAST) as p:
        assert os.path.getsize(p) > 0
        kept = p
    assert not os.path.exists(kept)  # temp dir removed


@needs_ffmpeg
def test_hls_is_remuxed(server):
    with materialize(f"{server}/hls/index.m3u8", FAST) as p:
        assert p.endswith(".mp4") and os.path.getsize(p) > 0


@needs_ffmpeg
def test_transient_5xx_is_retried(server):
    _Handler.flaky_left["n"] = 2
    with materialize(f"{server}/flaky.mp4", FAST) as p:
        assert os.path.getsize(p) > 0


def test_retries_exhausted_raise_transient(server):
    _Handler.flaky_left["n"] = 10
    with pytest.raises(TransientSourceError):
        with materialize(f"{server}/flaky.mp4", FAST):
            pass
    _Handler.flaky_left["n"] = 0


@pytest.mark.parametrize("path", ["/missing.mp4", "/forbidden.mp4"])
def test_4xx_is_permanent(server, path):
    with pytest.raises(PermanentSourceError):
        with materialize(server + path, FAST):
            pass


def test_too_large_is_permanent(server):
    with pytest.raises(PermanentSourceError, match="too large"):
        with materialize(f"{server}/huge.mp4", FAST):
            pass


@needs_ffmpeg
def test_html_page_is_not_a_video(server):
    with pytest.raises(PermanentSourceError, match="not a video"):
        with materialize(f"{server}/page.html", FAST):
            pass


def test_private_hosts_blocked_by_default(server):
    with pytest.raises(PermanentSourceError, match="non-public"):
        with materialize(f"{server}/clip.mp4", SourceOptions()):
            pass


def test_allowed_hosts(server):
    opt = SourceOptions(allow_private_hosts=True, allowed_hosts=("cdn.example.com",))
    with pytest.raises(PermanentSourceError, match="ALLOWED_HOSTS"):
        with materialize(f"{server}/clip.mp4", opt):
            pass


def test_default_video_id():
    assert default_video_id("https://cdn.example.com/a/b/movie-01.mp4?sig=x") == "movie-01"
    assert default_video_id("https://cdn.example.com/show/index.m3u8") == "index"
    assert default_video_id("/data/clip.mov") == "clip"


@needs_ffmpeg
def test_cli_analyzes_url(server, tmp_path, monkeypatch):
    monkeypatch.setenv("ADVIDEO_ALLOW_PRIVATE_HOSTS", "1")
    out = tmp_path / "map.json"
    rc = cli.main(["--video", f"{server}/clip.mp4", "--out", str(out)])
    assert rc == cli.EXIT_OK
    data = json.loads(out.read_text())
    assert data["videoId"] == "clip" and data["segments"]


def test_cli_exit_codes(server, monkeypatch, capsys):
    monkeypatch.setenv("ADVIDEO_ALLOW_PRIVATE_HOSTS", "1")
    monkeypatch.setenv("ADVIDEO_DOWNLOAD_RETRIES", "1")
    assert cli.main(["--video", f"{server}/missing.mp4"]) == cli.EXIT_SOURCE_PERMANENT
    assert "SOURCE_ERROR permanent" in capsys.readouterr().err
    _Handler.flaky_left["n"] = 5
    assert cli.main(["--video", f"{server}/flaky.mp4"]) == cli.EXIT_SOURCE_TRANSIENT
    _Handler.flaky_left["n"] = 0
