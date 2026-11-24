"""R8c: semantic brand safety for transcript text (multilingual sentence embeddings).

The lexicon (textmatch.safety_detections) is precise but misses paraphrases ("rescuers are digging through
the rubble", "zraneniam podľahol"). This classifier embeds each Whisper segment with a multilingual
sentence-embedding model and compares it with short example sentences of the active language packs
(``schema/languages/<code>.json`` ``semanticSafety``): sensitive prototypes per subtype vs. everyday neutral ones.
Model, top-k and thresholds are in ``schema/taxonomy.json`` ``semanticSafety``.

    score = mean(top-k cosine to sensitive) - mean(top-k cosine to neutral)

score >= ``thresholds.high`` -> a high-severity hit (marks the 6 s window alone), >= ``thresholds.medium`` ->
medium (needs a lexicon hit in the window or a neighbouring one; semantic hits never corroborate each other —
R8d real-broadcast finding: the classifier repeats on routine police/court talk). Hits are ``safety_speech`` detections with the
term ``speech: ~<subtype>`` so aggregate's window rule combines them with lexicon hits.

Heavy deps (torch, transformers) load lazily; set ``ADVIDEO_SEMANTIC_SAFETY=0`` to disable.
"""

from __future__ import annotations

import logging
import os
from dataclasses import dataclass

from .models import Detection
from . import languages
from .taxonomy import SEMANTIC_SAFETY_CONFIG

log = logging.getLogger(__name__)


@dataclass(frozen=True)
class Chunk:
    start: float
    end: float
    text: str


def build_chunks(segments: list[tuple[float, float, str]], span_sec: float, stride_sec: float) -> list[Chunk]:
    """R8e: sliding story chunks over transcript segments.

    A chunk starts every `stride_sec` and holds the segments that *start* within the next `span_sec`
    (so overlapping chunks share segments); identical chunks are dropped. Short Whisper segments alone are too
    little context — routine police talk ("the police are investigating") looked like violence — while a
    ~24 s chunk carries the story.
    """
    segs = sorted((float(a), float(b), (t or "").strip()) for a, b, t in segments if (t or "").strip())
    if not segs or span_sec <= 0 or stride_sec <= 0:
        return []
    out: list[Chunk] = []
    seen: set[tuple[float, float, int]] = set()
    t, last = 0.0, max(b for _, b, _ in segs)
    while t < last:
        cur = [s for s in segs if t <= s[0] < t + span_sec]
        if cur:
            key = (cur[0][0], cur[-1][0], len(cur))
            if key not in seen:
                seen.add(key)
                out.append(Chunk(cur[0][0], max(b for _, b, _ in cur), " ".join(x for _, _, x in cur)))
        t += stride_sec
    return out


@dataclass(frozen=True)
class SemanticHit:
    score: float
    subtype: str
    severity: str | None  # high | medium | None (below thresholds)


def enabled_by_env() -> bool:
    return os.environ.get("ADVIDEO_SEMANTIC_SAFETY", "1").lower() not in ("0", "false", "no", "off")


class SemanticSafety:
    def __init__(self, model_name: str | None = None, device: str | None = None,
                 high: float | None = None, medium: float | None = None, top_k: int | None = None):
        cfg = SEMANTIC_SAFETY_CONFIG
        langs = languages.active()  # prototypes of the active language packs
        self.languages = langs.codes
        self.model_name = model_name or cfg.get("model", "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2")
        th = cfg.get("thresholds", {})
        self.high = float(high if high is not None else th.get("high", 0.2))
        self.medium = float(medium if medium is not None else th.get("medium", 0.12))
        self.top_k = int(top_k or cfg.get("topK", 3))
        # R8e: "chunk" = classify ~24 s story chunks (default), "segment" = each Whisper segment (R8c behaviour)
        ch = cfg.get("chunk", {})
        self.unit = str(cfg.get("unit", "segment")).lower()
        self.chunk_span = float(ch.get("spanSec", 24))
        self.chunk_stride = float(ch.get("strideSec", 8))
        self.max_length = int(cfg.get("maxTokens", 256 if self.unit == "chunk" else 128))

        import torch
        from transformers import AutoModel, AutoTokenizer

        self._torch = torch
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        self._tok = AutoTokenizer.from_pretrained(self.model_name)
        self._model = AutoModel.from_pretrained(self.model_name).to(self.device).eval()

        self._pos_labels: list[str] = []
        pos_texts: list[str] = []
        for subtype, examples in langs.semantic_sensitive.items():
            pos_texts += examples
            self._pos_labels += [subtype] * len(examples)
        neg_texts = list(langs.semantic_neutral)
        if not pos_texts or not neg_texts:
            raise ValueError("active language packs have no semanticSafety 'sensitive'/'neutral' examples")
        self._pos = self.embed(pos_texts)
        self._neg = self.embed(neg_texts)

    def embed(self, texts: list[str], batch_size: int = 32):
        torch = self._torch
        out = []
        for i in range(0, len(texts), batch_size):
            b = self._tok(texts[i:i + batch_size], padding=True, truncation=True, max_length=getattr(self, "max_length", 128), return_tensors="pt")
            b = {k: v.to(self.device) for k, v in b.items()}
            with torch.no_grad():
                h = self._model(**b).last_hidden_state
            mask = b["attention_mask"].unsqueeze(-1).float()
            v = (h * mask).sum(1) / mask.sum(1).clamp(min=1e-9)
            out.append(torch.nn.functional.normalize(v, dim=1))
        return torch.cat(out) if out else torch.empty(0)

    def classify(self, texts: list[str]) -> list[SemanticHit]:
        texts = [t.strip() for t in texts]
        idx = [i for i, t in enumerate(texts) if len(t.split()) >= 3]  # too short to judge
        hits = [SemanticHit(0.0, "", None) for _ in texts]
        if not idx:
            return hits
        e = self.embed([texts[i] for i in idx])
        sp = e @ self._pos.T
        sn = e @ self._neg.T
        k_pos = min(self.top_k, sp.shape[1])
        k_neg = min(self.top_k, sn.shape[1])
        top_pos = sp.topk(k_pos, dim=1)
        score = top_pos.values.mean(1) - sn.topk(k_neg, dim=1).values.mean(1)
        for row, i in enumerate(idx):
            s = float(score[row])
            subtype = self._pos_labels[int(top_pos.indices[row, 0])]
            sev = "high" if s >= self.high else "medium" if s >= self.medium else None
            hits[i] = SemanticHit(round(s, 3), subtype, sev)
        return hits

    def detections(self, texts: list[str], confidence: list[float] | None = None) -> list[list[Detection]]:
        """Per input text: [] or one safety_speech Detection ("speech: ~war")."""
        out: list[list[Detection]] = []
        for i, h in enumerate(self.classify(texts)):
            if h.severity is None:
                out.append([])
                continue
            conf = confidence[i] if confidence else 0.8
            out.append([Detection(
                term=f"speech: ~{h.subtype}",
                score=round(min(1.0, conf), 3),
                kind="safety_speech",
                category=h.subtype,
                categories=[h.subtype, "sensitive"],
                severity=h.severity,
            )])
        return out

    def chunk_detections(self, segments: list[tuple[float, float, str]],
                         confidence: float = 0.8) -> list[tuple[float, float, Detection]]:
        """R8e: classify sliding story chunks; each hit spans its chunk (aggregate adds it to every 6 s window
        it overlaps, where the usual severity rules apply: high alone, medium needs a lexicon word)."""
        chunks = build_chunks(segments, self.chunk_span, self.chunk_stride)
        dets = self.detections([c.text for c in chunks], [confidence] * len(chunks))
        return [(c.start, c.end, d) for c, ds in zip(chunks, dets) for d in ds]

    def track_detections(self, segments: list[tuple[float, float, str]],
                         confidences: list[float] | None = None) -> list[tuple[float, float, Detection]]:
        """Timed semantic detections for a whole transcript, per the configured unit (chunk | segment)."""
        if self.unit == "chunk":
            return self.chunk_detections(segments)
        per = self.detections([t for _, _, t in segments], confidences)
        return [(a, b, d) for (a, b, _), ds in zip(segments, per) for d in ds]

    def close(self) -> None:
        self._model = None
        self._pos = self._neg = None


def load_or_none(**kwargs) -> SemanticSafety | None:
    """SemanticSafety when enabled and its deps/model are available, else None (logged)."""
    if not enabled_by_env():
        return None
    try:
        return SemanticSafety(**kwargs)
    except Exception as exc:  # missing transformers, offline without cached model, …
        log.warning("semantic safety disabled: %s", exc)
        return None
