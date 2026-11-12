"""Pure text-matching helpers (no ML) — brand and keyword extraction.

Kept separate from the OCR/Whisper backends so this logic is fully unit-testable
without any models: the backends just feed recognized/transcribed text in here.
"""

from __future__ import annotations

import re

from .models import Detection
from .taxonomy import BRAND_LEXICON, TERM_CATEGORIES

_WORD_RE = re.compile(r"[a-z0-9]+")


def normalize(text: str) -> str:
    """Lowercase and collapse to single-space-separated alphanumeric tokens."""
    return " ".join(_WORD_RE.findall(text.lower()))


def _contains_phrase(norm: str, phrase: str) -> bool:
    # word-boundary match that also works for multi-word phrases
    return f" {phrase} " in f" {norm} "


def match_brands(text: str) -> list[tuple[str, list[str]]]:
    """Return [(canonical_label, categories)] for brands present in `text`."""
    norm = normalize(text)
    hits: dict[str, list[str]] = {}
    for alias, (label, cats) in BRAND_LEXICON.items():
        if _contains_phrase(norm, normalize(alias)):
            hits[label] = cats
    return list(hits.items())


def match_keywords(text: str) -> list[tuple[str, list[str]]]:
    """Return [(term, categories)] for taxonomy terms present in `text`."""
    norm = normalize(text)
    hits: dict[str, list[str]] = {}
    for term, cats in TERM_CATEGORIES.items():
        if _contains_phrase(norm, normalize(term)):
            hits[term] = cats
    return list(hits.items())


def brand_detections(text: str, kind: str, score: float) -> list[Detection]:
    """Brand entities found in text, as Detections of the given kind.

    kind: "text" for OCR (on-screen), "audio" for Whisper (spoken).
    """
    out: list[Detection] = []
    for label, cats in match_brands(text):
        out.append(
            Detection(
                term=label,
                score=score,
                kind=kind,
                category=cats[0] if cats else None,
                categories=list(cats),
                entity_type="brand",
                entity_label=label,
            )
        )
    return out


def keyword_detections(text: str, kind: str, score: float) -> list[Detection]:
    """Taxonomy keywords found in text, as Detections of the given kind."""
    out: list[Detection] = []
    for term, cats in match_keywords(text):
        out.append(
            Detection(
                term=term,
                score=score,
                kind=kind,
                category=cats[0] if cats else None,
                categories=list(cats),
            )
        )
    return out
