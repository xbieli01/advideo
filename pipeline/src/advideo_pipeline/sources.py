"""Video source resolution: turn a job's ``videoUri`` into a local file the analyzers can read.

Supported inputs
----------------
* local path or ``file://`` URI            -> used in place (must exist)
* ``http(s)://`` direct file (mp4, mov, …) -> streamed to a temp file (size limit, retries)
* ``http(s)://…/*.m3u8`` HLS playlist       -> remuxed to a temp mp4 by ffmpeg (no re-encode)

Analyzers make several passes over the video (ffprobe, frame sampling, OCR, Whisper), so remote
sources are materialized once instead of being streamed repeatedly.

Errors are classified for the orchestrator's retry policy:
* :class:`PermanentSourceError` – retrying will not help (404, not a video, too large, blocked host)
* :class:`TransientSourceError` – network/5xx/timeouts; retried here with backoff, then surfaced

Configuration (environment)
---------------------------
``ADVIDEO_MAX_DOWNLOAD_BYTES``   max size of a downloaded/remuxed file (default 5 GiB)
``ADVIDEO_DOWNLOAD_TIMEOUT``     socket timeout in seconds per read (default 60)
``ADVIDEO_HLS_TIMEOUT``          max seconds for an HLS remux (default 3600)
``ADVIDEO_DOWNLOAD_RETRIES``     attempts for transient errors (default 3)
``ADVIDEO_ALLOWED_HOSTS``        optional comma list of allowed hosts (``cdn.example.com``, ``*.example.com``)
``ADVIDEO_ALLOW_PRIVATE_HOSTS``  ``1`` to allow loopback/private addresses (default: blocked, SSRF guard)
"""

from __future__ import annotations

import contextlib
import ipaddress
import os
import shutil
import socket
import subprocess
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Iterator
from dataclasses import dataclass

__all__ = [
    "SourceError",
    "PermanentSourceError",
    "TransientSourceError",
    "SourceOptions",
    "materialize",
    "is_remote",
    "default_video_id",
]

_CHUNK = 1024 * 1024
_HLS_CONTENT_TYPES = ("application/vnd.apple.mpegurl", "application/x-mpegurl", "audio/mpegurl")


class SourceError(Exception):
    """Base class; ``transient`` tells the orchestrator whether a retry may succeed."""

    transient = False


class PermanentSourceError(SourceError):
    transient = False


class TransientSourceError(SourceError):
    transient = True


def _env_int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, "") or default)
    except ValueError:
        return default


@dataclass(frozen=True)
class SourceOptions:
    max_bytes: int = 5 * 1024**3
    timeout_sec: float = 60.0
    hls_timeout_sec: float = 3600.0
    retries: int = 3
    backoff_sec: float = 2.0
    allowed_hosts: tuple[str, ...] = ()
    allow_private_hosts: bool = False

    @classmethod
    def from_env(cls) -> "SourceOptions":
        hosts = tuple(h.strip().lower() for h in os.environ.get("ADVIDEO_ALLOWED_HOSTS", "").split(",") if h.strip())
        return cls(
            max_bytes=_env_int("ADVIDEO_MAX_DOWNLOAD_BYTES", cls.max_bytes),
            timeout_sec=float(_env_int("ADVIDEO_DOWNLOAD_TIMEOUT", int(cls.timeout_sec))),
            hls_timeout_sec=float(_env_int("ADVIDEO_HLS_TIMEOUT", int(cls.hls_timeout_sec))),
            retries=max(1, _env_int("ADVIDEO_DOWNLOAD_RETRIES", cls.retries)),
            allowed_hosts=hosts,
            allow_private_hosts=os.environ.get("ADVIDEO_ALLOW_PRIVATE_HOSTS", "") in ("1", "true", "yes"),
        )


def is_remote(uri: str) -> bool:
    return urllib.parse.urlsplit(uri).scheme.lower() in ("http", "https")


def default_video_id(uri: str) -> str:
    """File name without extension, also for URLs (query string ignored)."""
    path = urllib.parse.urlsplit(uri).path if is_remote(uri) else uri
    if path.startswith("file://"):
        path = urllib.parse.urlsplit(path).path
    base = os.path.basename(path.rstrip("/"))
    return os.path.splitext(base)[0] or "video"


@contextlib.contextmanager
def materialize(uri: str, options: SourceOptions | None = None) -> Iterator[str]:
    """Yield a local file path for ``uri``; temp files are removed on exit."""
    opt = options or SourceOptions.from_env()
    parts = urllib.parse.urlsplit(uri)
    scheme = parts.scheme.lower()

    if scheme in ("", "file") or (len(scheme) == 1 and os.name == "nt"):  # "C:\..." on Windows
        path = urllib.parse.unquote(parts.path) if scheme == "file" else uri
        if not os.path.isfile(path):
            raise PermanentSourceError(f"video file not found: {path}")
        yield path
        return

    if scheme not in ("http", "https"):
        raise PermanentSourceError(f"unsupported URI scheme '{scheme}' (use a local path, http or https)")

    _check_host(parts.hostname or "", opt)

    workdir = tempfile.mkdtemp(prefix="advideo-src-")
    try:
        if _looks_like_hls(parts.path):
            local = _with_retries(lambda: _remux_hls(uri, workdir, opt), opt)
        else:
            local = _with_retries(lambda: _download(uri, workdir, opt), opt)
        yield local
    finally:
        shutil.rmtree(workdir, ignore_errors=True)


# --------------------------------------------------------------------------- host checks

def _check_host(host: str, opt: SourceOptions) -> None:
    host = host.lower()
    if not host:
        raise PermanentSourceError("URL has no host")
    if opt.allowed_hosts and not any(_host_matches(host, pat) for pat in opt.allowed_hosts):
        raise PermanentSourceError(f"host '{host}' is not in ADVIDEO_ALLOWED_HOSTS")
    if opt.allow_private_hosts:
        return
    try:
        infos = socket.getaddrinfo(host, None)
    except socket.gaierror as exc:
        raise TransientSourceError(f"cannot resolve host '{host}': {exc}") from exc
    for info in infos:
        ip = ipaddress.ip_address(info[4][0])
        if ip.is_private or ip.is_loopback or ip.is_link_local or ip.is_reserved or ip.is_multicast:
            raise PermanentSourceError(
                f"host '{host}' resolves to a non-public address ({ip}); set ADVIDEO_ALLOW_PRIVATE_HOSTS=1 to allow")


def _host_matches(host: str, pattern: str) -> bool:
    if pattern.startswith("*."):
        return host.endswith(pattern[1:]) or host == pattern[2:]
    return host == pattern


# --------------------------------------------------------------------------- retries

def _with_retries(fn, opt: SourceOptions) -> str:
    last: TransientSourceError | None = None
    for attempt in range(1, opt.retries + 1):
        try:
            return fn()
        except TransientSourceError as exc:
            last = exc
            if attempt < opt.retries:
                time.sleep(min(getattr(exc, "retry_after", 0) or opt.backoff_sec * 2 ** (attempt - 1), 60))
    assert last is not None
    raise last


def _looks_like_hls(path: str) -> bool:
    return path.lower().endswith(".m3u8")


# --------------------------------------------------------------------------- direct download

def _download(uri: str, workdir: str, opt: SourceOptions) -> str:
    req = urllib.request.Request(uri, headers={"User-Agent": "advideo-pipeline/1.0"})
    try:
        resp = urllib.request.urlopen(req, timeout=opt.timeout_sec)  # noqa: S310 (scheme checked)
    except urllib.error.HTTPError as exc:
        raise _http_error(exc, uri) from exc
    except (urllib.error.URLError, TimeoutError, ConnectionError, socket.timeout) as exc:
        raise TransientSourceError(f"download failed for {uri}: {exc}") from exc

    with resp:
        ctype = (resp.headers.get("Content-Type") or "").split(";")[0].strip().lower()
        if ctype in _HLS_CONTENT_TYPES:
            return _remux_hls(uri, workdir, opt)
        length = resp.headers.get("Content-Length")
        if length and length.isdigit() and int(length) > opt.max_bytes:
            raise PermanentSourceError(f"video too large: {int(length)} bytes > limit {opt.max_bytes}")

        suffix = os.path.splitext(urllib.parse.urlsplit(uri).path)[1] or ".mp4"
        local = os.path.join(workdir, "source" + suffix[:8])
        total = 0
        try:
            with open(local, "wb") as fh:
                while chunk := resp.read(_CHUNK):
                    total += len(chunk)
                    if total > opt.max_bytes:
                        raise PermanentSourceError(f"video too large: > limit {opt.max_bytes} bytes")
                    fh.write(chunk)
        except (TimeoutError, ConnectionError, socket.timeout, urllib.error.URLError) as exc:
            raise TransientSourceError(f"download interrupted for {uri} after {total} bytes: {exc}") from exc

    if length and length.isdigit() and total != int(length):
        raise TransientSourceError(f"incomplete download for {uri}: {total}/{length} bytes")
    if total == 0:
        raise PermanentSourceError(f"empty response for {uri}")
    _check_is_video(local, uri)
    return local


def _http_error(exc: urllib.error.HTTPError, uri: str) -> SourceError:
    if exc.code in (408, 425, 429) or exc.code >= 500:
        err = TransientSourceError(f"HTTP {exc.code} for {uri}")
        retry_after = exc.headers.get("Retry-After") if exc.headers else None
        if retry_after and retry_after.isdigit():
            err.retry_after = int(retry_after)  # type: ignore[attr-defined]
        return err
    return PermanentSourceError(f"HTTP {exc.code} for {uri}")


def _check_is_video(local: str, uri: str) -> None:
    """Reject HTML error pages etc. (only when ffprobe is available)."""
    if not shutil.which("ffprobe"):
        return
    res = subprocess.run(
        ["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_type",
         "-of", "csv=p=0", local],
        capture_output=True, text=True, timeout=120,
    )
    if "video" not in res.stdout:
        raise PermanentSourceError(f"downloaded content is not a video: {uri}")


# --------------------------------------------------------------------------- HLS

def _remux_hls(uri: str, workdir: str, opt: SourceOptions) -> str:
    if not shutil.which("ffmpeg"):
        raise PermanentSourceError("ffmpeg is required to read HLS sources")
    local = os.path.join(workdir, "source-hls.mp4")
    cmd = [
        "ffmpeg", "-nostdin", "-y", "-loglevel", "error",
        "-rw_timeout", str(int(opt.timeout_sec * 1_000_000)),
        "-protocol_whitelist", "http,https,tcp,tls,crypto",
        "-i", uri,
        "-map", "0:v:0?", "-map", "0:a:0?", "-c", "copy",
        "-fs", str(opt.max_bytes),
        local,
    ]
    try:
        res = subprocess.run(cmd, capture_output=True, text=True, timeout=opt.hls_timeout_sec)
    except subprocess.TimeoutExpired as exc:
        raise TransientSourceError(f"HLS remux timed out after {opt.hls_timeout_sec:.0f}s: {uri}") from exc
    if res.returncode != 0 or not os.path.isfile(local) or os.path.getsize(local) == 0:
        err = (res.stderr or "").strip().splitlines()[-1:] or ["unknown error"]
        msg = f"HLS remux failed for {uri}: {err[0]}"
        if any(code in res.stderr for code in ("400 Bad Request", "401 Unauthorized", "403 Forbidden",
                                                "404 Not Found", "410 Gone", "Invalid data found")):
            raise PermanentSourceError(msg)
        raise TransientSourceError(msg)
    if os.path.getsize(local) >= opt.max_bytes:
        raise PermanentSourceError(f"HLS video too large: reached limit {opt.max_bytes} bytes")
    _check_is_video(local, uri)
    return local
