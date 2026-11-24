"""OCR brand analyzer — reads on-screen text and extracts brand entities.

Frame-level analyzer (needs pixels). Uses EasyOCR (lazy import) to read text on
each sampled frame, then the pure `textmatch` layer turns recognized text into
brand entities and taxonomy keywords. Install with `pip install -e .[ml]`.

Value for the product: an on-screen "TESLA" wordmark or a logo caption becomes a
brand entity, enabling exact brand-match ads.
"""

from __future__ import annotations

from ..models import Detection, Frame
from ..textmatch import brand_detections, context_detections, keyword_detections, news_detections, normalize, safety_detections
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
    # class-level defaults (instances built without __init__ in tests keep working)
    detect_scale = 1.0
    static_diff = 0.0
    _last_thumb = None
    _last_result: list = []

    def __init__(
        self,
        languages: tuple[str, ...] | None = None,
        gpu: bool | None = None,
        min_confidence: float = 0.4,
        interval_sec: float = 2.0,
        max_side: int = 960,
        detect_scale: float = 0.6,
        static_diff: float = 4.0,
    ) -> None:
        """`interval_sec`: run OCR at most this often (text/logos stay on screen for seconds; OCR is the
        slowest backend). `max_side`: downscale frames whose longer side exceeds this (0 = off).
        `languages`: EasyOCR codes; None = from the active language packs (ADVIDEO_OCR_LANGUAGES / ADVIDEO_LANGUAGES).
        R4: text detection (CRAFT) is ~95 % of the OCR cost and scales with pixels -> `detect_scale` runs detection
        on a 0.6x image (EasyOCR mag_ratio) while recognition still reads the 960 px crops: 0.74 -> 0.53 s per frame,
        same text as detecting at 640 on the brand clips; `static_diff`: when the frame barely changed since the last OCR run
        (mean abs difference of a 64x36 grey thumbnail < this, 0-255 scale) the previous result is reused instead of
        running OCR again (0 = off). Talking heads, static shots and long overlays then cost almost nothing."""
        import easyocr  # lazy

        from ..device import pick_device

        # EasyOCR takes False (CPU), True (CUDA/MPS auto) or a device string
        dev = pick_device()
        use_gpu = (dev if dev != "cpu" else False) if gpu is None else gpu
        self.device = dev if use_gpu else "cpu"
        if languages is None:
            from ..languages import ocr_languages

            languages = tuple(ocr_languages())
        self.languages = tuple(languages)
        self._reader = easyocr.Reader(list(self.languages), gpu=use_gpu)
        self.min_confidence = min_confidence
        self.interval_sec = interval_sec
        self.max_side = max_side
        self.detect_scale = detect_scale
        self.static_diff = static_diff
        self._next_t = 0.0
        self._last_t = -1.0
        self._last_thumb = None
        self._last_result: list[tuple[str, float]] = []
        self.stats = {"ocr": 0, "reused": 0}

    def analyze(self, frame: Frame) -> list[Detection]:
        if frame.image is None:
            return []
        if frame.time_sec < self._last_t:  # a new video started (instance reused across videos)
            self._next_t = 0.0
            self._last_thumb = None
        self._last_t = frame.time_sec
        if frame.time_sec + 1e-6 < self._next_t:
            return []
        self._next_t = frame.time_sec + self.interval_sec
        image = frame.image
        thumb = _thumb(image) if self.static_diff else None
        if thumb is not None and self._last_thumb is not None and _diff(thumb, self._last_thumb) < self.static_diff:
            if hasattr(self, "stats"):
                self.stats["reused"] += 1
            return self._detections(self._last_result)
        if self.max_side:
            h, w = image.shape[:2]
            if max(h, w) > self.max_side:
                import cv2  # lazy

                f = self.max_side / max(h, w)
                image = cv2.resize(image, (round(w * f), round(h * f)), interpolation=cv2.INTER_AREA)
        # EasyOCR returns [(bbox, text, confidence), ...]
        results = [(t, c) for _, t, c in self._reader.readtext(image, mag_ratio=self.detect_scale)
                   if c >= self.min_confidence]
        if hasattr(self, "stats"):
            self.stats["ocr"] += 1
        self._last_thumb, self._last_result = thumb, results
        return self._detections(results)

    def _detections(self, results: list[tuple[str, float]]) -> list[Detection]:
        if not results:
            return []
        joined = " ".join(t for t, _ in results)
        score = max(c for _, c in results)

        dets: list[Detection] = []
        dets += brand_detections(joined, kind="text", score=round(float(score), 3))
        dets += keyword_detections(joined, kind="text", score=round(float(score), 3))
        dets += safety_detections(joined, kind="text", score=round(float(score), 3))
        dets += news_detections(joined, kind="text", score=round(float(score), 3))
        dets += context_detections(joined, kind="text", score=round(float(score), 3))
        # keep a light raw-text signal (deduped alnum tokens) for debugging/relevance
        for tok in dict.fromkeys(normalize(joined).split()):
            if len(tok) > 2 and tok not in _STOPWORDS and not tok.isdigit():
                dets.append(Detection(term=tok, score=round(float(score), 3), kind="text"))
        return dets

    def close(self) -> None:
        self._reader = None


def _thumb(image):
    import cv2  # lazy

    grey = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY) if image.ndim == 3 else image
    return cv2.resize(grey, (64, 36), interpolation=cv2.INTER_AREA).astype("float32")


def _diff(a, b) -> float:
    return float(abs(a - b).mean())

