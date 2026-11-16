"""Logo / brand analyzer: CLIP zero-shot over brand prompts on frame crops.

OCR only finds brands written as plain text; logos (Nike swoosh, Audi rings, VW badge, stylised TESLA
lettering) need a visual model. This backend scores every brand of the taxonomy (several prompts per
brand, averaged) against neutral distractor prompts ("a photo with no logo", "plain clothing", ...)
with one softmax, on the full frame plus a grid of crops (small logos are invisible to CLIP at full-frame
scale), and keeps the best crop per brand.

Detections are kind "logo" (visual) -> the aggregate temporal support filter applies, so a brand needs
>= 3 frames per window or a near-certain single hit (aggregate logo_min_support). Install with `pip install -e .[ml]`.
"""

from __future__ import annotations

from ..device import pick_device
from ..models import Detection, Frame
from ..taxonomy import BRAND_CATEGORIES, BRAND_CLIP_PROMPTS, CLIP_BRAND_NEUTRAL
from .base import Analyzer


class ClipBrandAnalyzer(Analyzer):
    name = "clip-brand"
    needs_frames = True

    def __init__(
        self,
        clip_model: str = "ViT-B-32",
        clip_pretrained: str = "laion2b_s34b_b79k",
        device: str | None = None,
        threshold: float = 0.7,
        grid: int = 2,
        interval_sec: float = 1.0,
        brands: list[str] | None = None,
    ) -> None:
        import torch  # lazy

        from ..clip_cache import load_clip

        self.device = pick_device(device)
        self._torch = torch
        self.threshold = threshold
        self.grid = grid
        self.interval_sec = interval_sec
        self._next_t, self._last_t = 0.0, -1.0
        self._clip, self._preprocess, self._tokenizer, self._logit_scale = load_clip(
            clip_model, clip_pretrained, self.device
        )
        self._brands = [b for b in BRAND_CLIP_PROMPTS if brands is None or b in brands]
        with torch.no_grad():
            vecs = []
            for b in self._brands:  # one vector per brand = mean of its prompt embeddings
                e = self._encode_text(BRAND_CLIP_PROMPTS[b])
                m = e.mean(dim=0)
                vecs.append(m / m.norm())
            for n in CLIP_BRAND_NEUTRAL:
                vecs.append(self._encode_text([n])[0])
            self._text = torch.stack(vecs)
        self._n_brands = len(self._brands)

    def _encode_text(self, prompts: list[str]):
        tok = self._tokenizer(prompts).to(self.device)
        f = self._clip.encode_text(tok)
        return f / f.norm(dim=-1, keepdim=True)

    def _crops(self, image):
        """Full frame + grid x grid tiles (with 15 % overlap) + centre crop, as PIL images."""
        import cv2
        from PIL import Image

        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        h, w = rgb.shape[:2]
        out = [rgb]
        g = self.grid
        if g > 1:
            th, tw = h // g, w // g
            oh, ow = int(th * 0.15), int(tw * 0.15)
            for r in range(g):
                for c in range(g):
                    y0, x0 = max(0, r * th - oh), max(0, c * tw - ow)
                    out.append(rgb[y0:min(h, (r + 1) * th + oh), x0:min(w, (c + 1) * tw + ow)])
            out.append(rgb[h // 4: 3 * h // 4, w // 4: 3 * w // 4])
        return [Image.fromarray(x) for x in out]

    def analyze(self, frame: Frame) -> list[Detection]:
        if frame.image is None:
            return []
        if frame.time_sec < self._last_t:  # new video (instance reused)
            self._next_t = 0.0
        self._last_t = frame.time_sec
        if frame.time_sec + 1e-6 < self._next_t:
            return []
        self._next_t = frame.time_sec + self.interval_sec

        torch = self._torch
        with torch.no_grad():
            batch = torch.stack([self._preprocess(c) for c in self._crops(frame.image)]).to(self.device)
            f = self._clip.encode_image(batch)
            f = f / f.norm(dim=-1, keepdim=True)
            probs = (self._logit_scale * f @ self._text.T).softmax(dim=-1)  # crops x (brands + neutral)
            best = probs[:, : self._n_brands].max(dim=0).values  # best crop per brand

        dets: list[Detection] = []
        for i, score in enumerate(best.tolist()):
            if score < self.threshold:
                continue
            label = self._brands[i]
            cats = BRAND_CATEGORIES.get(label, [])
            dets.append(Detection(term=label, score=round(score, 3), kind="logo",
                                  category=cats[0] if cats else None, categories=cats,
                                  entity_type="brand", entity_label=label))
        return dets

    def close(self) -> None:
        if self.device == "cuda":
            self._torch.cuda.empty_cache()
