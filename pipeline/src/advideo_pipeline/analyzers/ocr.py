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


class OcrBrandAnalyzer(Analyzer):
    name = "ocr"
    needs_frames = True

    def __init__(
        self,
        languages: tuple[str, ...] = ("en",),
        gpu: bool | None = None,
        min_confidence: float = 0.4,
    ) -> None:
        import torch  # lazy
        import easyocr  # lazy

        use_gpu = torch.cuda.is_available() if gpu is None else gpu
        self._reader = easyocr.Reader(list(languages), gpu=use_gpu)
        self.min_confidence = min_confidence

    def analyze(self, frame: Frame) -> list[Detection]:
        if frame.image is None:
            return []
        # EasyOCR returns [(bbox, text, confidence), ...]
        results = self._reader.readtext(frame.image)
        texts = [t for _, t, conf in results if conf >= self.min_confidence]
        if not texts:
            return []
        joined = " ".join(texts)
        score = max(conf for _, _, conf in results)

        dets: list[Detection] = []
        dets += brand_detections(joined, kind="text", score=round(float(score), 3))
        dets += keyword_detections(joined, kind="text", score=round(float(score), 3))
        # keep a light raw-text signal (deduped alnum tokens) for debugging/relevance
        for tok in dict.fromkeys(normalize(joined).split()):
            if len(tok) > 2:
                dets.append(Detection(term=tok, score=round(float(score), 3), kind="text"))
        return dets

    def close(self) -> None:
        self._reader = None
