"""R8c: semantic brand safety for transcript text (multilingual sentence embeddings).

The lexicon (textmatch.safety_detections) is precise but misses paraphrases ("rescuers are digging through
the rubble", "zraneniam podľahol"). This classifier embeds each Whisper segment with a multilingual
sentence-embedding model and compares it with short example sentences from ``schema/taxonomy.json``
(``semanticSafety``): sensitive prototypes per subtype vs. everyday neutral prototypes.

    score = mean(top-k cosine to sensitive) - mean(top-k cosine to neutral)

score >= ``thresholds.high`` -> a high-severity hit (marks the 6 s window alone), >= ``thresholds.medium`` ->
medium (the window needs one more hit, e.g. a lexicon word). Hits are ``safety_speech`` detections with the
term ``speech: ~<subtype>`` so aggregate's window rule combines them with lexicon hits.

Heavy deps (torch, transformers) load lazily; set ``ADVIDEO_SEMANTIC_SAFETY=0`` to disable.
"""

from __future__ import annotations

import logging
import os
from dataclasses import dataclass

from .models import Detection
from .taxonomy import SEMANTIC_SAFETY

log = logging.getLogger(__name__)


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
        cfg = SEMANTIC_SAFETY
        self.model_name = model_name or cfg.get("model", "sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2")
        th = cfg.get("thresholds", {})
        self.high = float(high if high is not None else th.get("high", 0.2))
        self.medium = float(medium if medium is not None else th.get("medium", 0.12))
        self.top_k = int(top_k or cfg.get("topK", 3))

        import torch
        from transformers import AutoModel, AutoTokenizer

        self._torch = torch
        self.device = device or ("cuda" if torch.cuda.is_available() else "cpu")
        self._tok = AutoTokenizer.from_pretrained(self.model_name)
        self._model = AutoModel.from_pretrained(self.model_name).to(self.device).eval()

        self._pos_labels: list[str] = []
        pos_texts: list[str] = []
        for subtype, examples in cfg.get("sensitive", {}).items():
            pos_texts += examples
            self._pos_labels += [subtype] * len(examples)
        neg_texts = list(cfg.get("neutral", []))
        if not pos_texts or not neg_texts:
            raise ValueError("taxonomy semanticSafety needs 'sensitive' and 'neutral' examples")
        self._pos = self.embed(pos_texts)
        self._neg = self.embed(neg_texts)

    def embed(self, texts: list[str], batch_size: int = 32):
        torch = self._torch
        out = []
        for i in range(0, len(texts), batch_size):
            b = self._tok(texts[i:i + batch_size], padding=True, truncation=True, max_length=128, return_tensors="pt")
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
