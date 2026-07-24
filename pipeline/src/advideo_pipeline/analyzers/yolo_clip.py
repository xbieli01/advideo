"""Real analyzer: YOLO object detection + CLIP zero-shot scene/action labels.

Heavy deps (torch, ultralytics, open_clip, opencv) are imported lazily inside
`__init__`, so importing this module never pulls them in — the mock pipeline and
its tests stay dependency-free. Install with: `pip install -e .[ml]`.

Design:
- YOLO gives concrete on-screen objects (COCO classes) with confidences.
- CLIP scores each frame against a fixed candidate label set (scenes/actions
  from taxonomy.CLIP_SCENE_LABELS) — zero-shot, no training.
- Both are mapped through `taxonomy` so categories are consistent with the mock.
- Device auto-selects CUDA when available (that's where throughput comes from);
  falls back to CPU.

Brands/logos and audio keywords (OCR + Whisper) are separate future backends
that plug in the same way; this one covers objects + scene/action labels.
"""

from __future__ import annotations

from ..models import Detection, Frame
from ..taxonomy import CLIP_SCENE_LABELS, categories_for
from .base import Analyzer


class YoloClipAnalyzer(Analyzer):
    name = "yolo+clip"
    needs_frames = True

    def __init__(
        self,
        yolo_weights: str = "yolov8n.pt",
        clip_model: str = "ViT-B-32",
        clip_pretrained: str = "laion2b_s34b_b79k",
        device: str | None = None,
        yolo_conf: float = 0.35,
        clip_threshold: float = 0.20,
        clip_topk: int = 3,
    ) -> None:
        import torch  # lazy
        from ultralytics import YOLO  # lazy
        import open_clip  # lazy

        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        self._torch = torch
        self.yolo_conf = yolo_conf
        self.clip_threshold = clip_threshold
        self.clip_topk = clip_topk

        # object detector
        self._yolo = YOLO(yolo_weights)

        # zero-shot label model
        self._clip, _, self._preprocess = open_clip.create_model_and_transforms(
            clip_model, pretrained=clip_pretrained, device=self.device
        )
        self._clip.eval()
        self._tokenizer = open_clip.get_tokenizer(clip_model)

        self._labels = [lbl for lbl, _ in CLIP_SCENE_LABELS]
        self._label_kind = dict(CLIP_SCENE_LABELS)
        prompts = [f"a photo of {lbl}" for lbl in self._labels]
        with torch.no_grad():
            tok = self._tokenizer(prompts).to(self.device)
            text_feats = self._clip.encode_text(tok)
            self._text_feats = text_feats / text_feats.norm(dim=-1, keepdim=True)

    def analyze(self, frame: Frame) -> list[Detection]:
        if frame.image is None:
            return []
        return self._yolo_dets(frame.image) + self._clip_dets(frame.image)

    # --- YOLO ---------------------------------------------------------------
    def _yolo_dets(self, image) -> list[Detection]:
        results = self._yolo.predict(
            image, conf=self.yolo_conf, verbose=False, device=self.device
        )
        dets: list[Detection] = []
        best: dict[str, float] = {}
        for r in results:
            names = r.names
            for box in r.boxes:
                label = names[int(box.cls)].lower()
                score = float(box.conf)
                best[label] = max(best.get(label, 0.0), score)
        for label, score in best.items():
            cats = categories_for(label)
            dets.append(
                Detection(
                    term=label,
                    score=round(score, 3),
                    kind="object",
                    category=cats[0] if cats else None,
                )
            )
        return dets

    # --- CLIP zero-shot -----------------------------------------------------
    def _clip_dets(self, image) -> list[Detection]:
        import cv2
        from PIL import Image

        torch = self._torch
        rgb = cv2.cvtColor(image, cv2.COLOR_BGR2RGB)
        pil = Image.fromarray(rgb)
        with torch.no_grad():
            img = self._preprocess(pil).unsqueeze(0).to(self.device)
            feats = self._clip.encode_image(img)
            feats = feats / feats.norm(dim=-1, keepdim=True)
            sims = (feats @ self._text_feats.T).squeeze(0)
            probs = sims.softmax(dim=-1)

        topk = min(self.clip_topk, len(self._labels))
        vals, idxs = probs.topk(topk)
        dets: list[Detection] = []
        for score, idx in zip(vals.tolist(), idxs.tolist()):
            if score < self.clip_threshold:
                continue
            label = self._labels[idx]
            cats = categories_for(label)
            dets.append(
                Detection(
                    term=label,
                    score=round(float(score), 3),
                    kind=self._label_kind[label],
                    category=cats[0] if cats else None,
                )
            )
        return dets

    def close(self) -> None:
        if self.device == "cuda":
            self._torch.cuda.empty_cache()
