"""Real analyzer: YOLO object detection + CLIP zero-shot scene/action labels.

Heavy deps (torch, ultralytics, open_clip, opencv) are imported lazily inside
`__init__`, so importing this module never pulls them in — the mock pipeline and
its tests stay dependency-free. Install with: `pip install -e .[ml]`.

Design:
- YOLO gives concrete on-screen objects (COCO classes) with confidences.
- CLIP scores each frame against a fixed candidate label set (scenes/actions
  from taxonomy.CLIP_SCENE_LABELS) — zero-shot, no training.
- Both are mapped through `taxonomy` so categories are consistent with the mock.
- Device: ADVIDEO_DEVICE env, else CUDA, else Apple MPS, else CPU (see device.py).

Brands/logos and audio keywords (OCR + Whisper) are separate future backends
that plug in the same way; this one covers objects + scene/action labels.
"""

from __future__ import annotations

from ..device import pick_device
from ..models import Detection, Frame
from ..taxonomy import CLIP_NEUTRAL_LABELS, CLIP_SCENE_LABELS, categories_for
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
        clip_threshold: float = 0.50,
        clip_topk: int = 3,
        class_conf: dict[str, float] | None = None,
        safety_threshold: float = 0.7,
    ) -> None:
        import torch  # lazy
        from ultralytics import YOLO  # lazy

        from ..clip_cache import load_clip

        self.device = pick_device(device)
        self._torch = torch
        self.yolo_conf = yolo_conf
        # per-class minimum confidence: screens are everywhere in the background (shop TVs, bus-shelter displays)
        # and low-confidence tv/laptop/phone boxes create false tech/electronics segments
        self.class_conf = {"tv": 0.6, "laptop": 0.6, "cell phone": 0.6} if class_conf is None else dict(class_conf)
        self.clip_threshold = clip_threshold
        self.clip_topk = clip_topk
        # brand-safety labels (kind "safety") use their own, stricter threshold: a false "sensitive" blocks ads
        self.kind_threshold = {"safety": safety_threshold}

        # object detector
        self._yolo = YOLO(yolo_weights)

        # zero-shot label model
        self._clip, self._preprocess, self._tokenizer, self._logit_scale = load_clip(
            clip_model, clip_pretrained, self.device
        )

        self._labels = [lbl for lbl, _ in CLIP_SCENE_LABELS]
        self._label_kind = dict(CLIP_SCENE_LABELS)
        # CLIP was trained with a learned temperature (logit_scale ~ 100); softmax over raw cosine
        # similarities (~0.2-0.3) is almost uniform and never passes the threshold.
        self._groups: dict[str, list[int]] = {}
        for i, lbl in enumerate(self._labels):
            self._groups.setdefault(self._label_kind[lbl], []).append(i)
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
            if score < self.class_conf.get(label, 0.0):
                continue
            cats = categories_for(label)
            dets.append(
                Detection(
                    term=label,
                    score=round(score, 3),
                    kind="object",
                    category=cats[0] if cats else None,
                    categories=cats,
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

        # softmax per label kind (action / scene / object): otherwise a dominant action ("running")
        # takes all probability mass and objects ("sneakers") never surface.
        dets: list[Detection] = []
        for kind, idxs in self._groups.items():
            threshold = self.kind_threshold.get(kind, self.clip_threshold)
            group = (self._logit_scale * sims[idxs]).softmax(dim=-1)
            k = min(self.clip_topk, len(idxs))
            vals, pos = group.topk(k)
            for score, p in zip(vals.tolist(), pos.tolist()):
                if score < threshold:
                    continue
                label = self._labels[idxs[p]]
                if label in CLIP_NEUTRAL_LABELS:
                    continue
                cats = categories_for(label)
                dets.append(
                    Detection(
                        term=label,
                        score=round(float(score), 3),
                        kind=self._label_kind[label],
                        category=cats[0] if cats else None,
                        categories=cats,
                    )
                )
        return dets

    def close(self) -> None:
        if self.device == "cuda":
            self._torch.cuda.empty_cache()
