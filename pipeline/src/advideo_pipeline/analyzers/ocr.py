"""OCR brand analyzer — reads on-screen text and extracts brand entities.

Frame-level analyzer (needs pixels). Uses EasyOCR (lazy import) to read text on
each sampled frame, then the pure `textmatch` layer turns recognized text into
brand entities and taxonomy keywords. Install with `pip install -e .[ml]`.

Value for the product: an on-screen "TESLA" wordmark or a logo caption becomes a
brand entity, enabling exact brand-match ads.
"""

from __future__ import annotations

from ..models import Detection, Frame
from ..textmatch import brand_detections, keyword_detections, normalize
from .base import Analyzer


# filler words that OCR of captions/overlays produces constantly; useless as ad keywords
_STOPWORDS = frozenset(
    "the and with your you for are was were this that from into until while well made have has had not "
    "but all any can will just our out its their them they then than there here what when where who how "
    "more most some such only own same very also".split()
)


class OcrBrandAnalyzer(Analyzer):
    name = "ocr"
    needs_frames = True

    def __init__(
        self,
        languages: tuple[str, ...] = ("en",),
        gpu: bool | None = None,
        min_confidence: float = 0.4,
        interval_sec: float = 2.0,
        max_side: int = 960,
    ) -> None:
        """`interval_sec`: run OCR at most this often (text/logos stay on screen for seconds; OCR is the
        slowest backend). `max_side`: downscale frames whose longer side exceeds this (0 = off)."""
        import easyocr  # lazy

        from ..device import pick_device

        # EasyOCR takes False (CPU), True (CUDA/MPS auto) or a device string
        dev = pick_device()
        use_gpu = (dev if dev != "cpu" else False) if gpu is None else gpu
        self.device = dev if use_gpu else "cpu"
        self._reader = easyocr.Reader(list(languages), gpu=use_gpu)
        self.min_confidence = min_confidence
        self.interval_sec = interval_sec
        self.max_side = max_side
        self._next_t = 0.0
        self._last_t = -1.0

    def analyze(self, frame: Frame) -> list[Detection]:
        if frame.image is None:
            return []
        if frame.time_sec < self._last_t:  # a new video started (instance reused across videos)
            self._next_t = 0.0
        self._last_t = frame.time_sec
        if frame.time_sec + 1e-6 < self._next_t:
            return []
        self._next_t = frame.time_sec + self.interval_sec
        image = frame.image
        if self.max_side:
            h, w = image.shape[:2]
            if max(h, w) > self.max_side:
                import cv2  # lazy

                f = self.max_side / max(h, w)
                image = cv2.resize(image, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)
        # EasyOCR returns [(bbox, text, confidence), ...]
        results = [(t, c) for _, t, c in self._reader.readtext(image) if c >= self.min_confidence]
        if not results:
            return []
        joined = " ".join(t for t, _ in results)
        score = max(c for _, c in results)

        dets: list[Detection] = []
        dets += brand_detections(joined, kind="text", score=round(float(score), 3))
        dets += keyword_detections(joined, kind="text", score=round(float(score), 3))
        # keep a light raw-text signal (deduped alnum tokens) for debugging/relevance
        for tok in dict.fromkeys(normalize(joined).split()):
            if len(tok) > 2 and tok not in _STOPWORDS and not tok.isdigit():
                dets.append(Detection(term=tok, score=round(float(score), 3), kind="text"))
        return dets

    def close(self) -> None:
        self._reader = None
