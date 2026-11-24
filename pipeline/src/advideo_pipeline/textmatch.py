"""Pure text-matching helpers (no ML) — brand and keyword extraction.

Kept separate from the OCR/Whisper backends so this logic is fully unit-testable
without any models: the backends just feed recognized/transcribed text in here.
"""

from __future__ import annotations

import re
import unicodedata
from functools import lru_cache

from .models import Detection
from . import languages
from .languages import SafetyTerm
from .taxonomy import BRAND_LEXICON, TERM_CATEGORIES

_WORD_RE = re.compile(r"[a-z0-9]+")
_NO_NFKD = str.maketrans({"ß": "ss", "ł": "l", "Ł": "L", "đ": "d", "Đ": "D", "ø": "o", "Ø": "O", "æ": "ae", "œ": "oe"})


def _strip_accents(text: str) -> str:
    # "Zabitých" -> "Zabitych", "Überschwemmung" -> "Uberschwemmung" (ß -> ss), Polish ł -> l (no NFKD form)
    text = text.translate(_NO_NFKD)
    return "".join(c for c in unicodedata.normalize("NFKD", text) if not unicodedata.combining(c))


def normalize(text: str) -> str:
    """Lowercase, drop diacritics and collapse to single-space-separated alphanumeric tokens."""
    return " ".join(_WORD_RE.findall(_strip_accents(text).lower()))


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
    """Return [(term, categories)] for taxonomy terms present in `text`.

    Besides the (English) taxonomy terms, local words from the active language packs map to them
    ("auto" -> "car" with the sk pack).
    """
    norm = normalize(text)
    hits: dict[str, list[str]] = {}
    for term, cats in TERM_CATEGORIES.items():
        if _contains_phrase(norm, normalize(term)):
            hits[term] = cats
    for local, term in languages.active().keywords.items():
        if term not in hits and term in TERM_CATEGORIES and _contains_phrase(norm, normalize(local)):
            hits[term] = TERM_CATEGORIES[term]
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


# --- brand safety from speech / on-screen text ---------------------------------

@lru_cache(maxsize=None)
def _pattern(phrase: str) -> tuple[tuple[str, bool], ...]:
    """'tragick* nehod*' -> (('tragick', True), ('nehod', True)); token, is_prefix."""
    out = []
    for raw in phrase.split():
        prefix = raw.endswith("*")
        tok = normalize(raw.rstrip("*"))
        if tok:
            out.append((tok, prefix))
    return tuple(out)


def _spans(tokens: list[str], phrase: str) -> list[tuple[int, int]]:
    """All (start, end) token spans where the pattern of `phrase` occurs."""
    pat = _pattern(phrase)
    n = len(pat)
    if n == 0:
        return []
    return [
        (i, i + n) for i in range(len(tokens) - n + 1)
        if all((tokens[i + j].startswith(t) if pre else tokens[i + j] == t) for j, (t, pre) in enumerate(pat))
    ]


def _find(tokens: list[str], phrase: str) -> str | None:
    spans = _spans(tokens, phrase)
    return " ".join(tokens[spans[0][0]:spans[0][1]]) if spans else None


def match_safety(text: str) -> list[tuple[str, SafetyTerm]]:
    """[(matched words, SafetyTerm)].

    Longest match wins: "car crash" (high) suppresses the "crash" (medium) inside it, so one phrase
    is never counted as two hits. Same surface form matched by several groups keeps the highest severity.
    """
    tokens = normalize(text).split()
    langs = languages.active()
    cands: list[tuple[int, int, SafetyTerm]] = [
        (a, b, st) for st in langs.safety_terms for a, b in _spans(tokens, st.phrase)
    ]
    cands.sort(key=lambda c: (-(c[1] - c[0]), -_SEV_RANK[c[2].severity]))
    # idioms ("zabili cas", "crash course") block every match inside them
    taken: list[tuple[int, int]] = [sp for ex in langs.exclusions for sp in _spans(tokens, ex)]
    excluded = len(taken)
    hits: dict[str, SafetyTerm] = {}
    for a, b, st in cands:
        if any(a >= x and b <= y for x, y in taken):
            continue  # inside an exclusion idiom or a longer (or equal, already taken) match
        if any(a < y and b > x for x, y in taken[:excluded]):
            continue  # overlaps an exclusion idiom
        taken.append((a, b))
        words = " ".join(tokens[a:b])
        prev = hits.get(words)
        if prev is None or _SEV_RANK[st.severity] > _SEV_RANK[prev.severity]:
            hits[words] = st
    return list(hits.items())


_SEV_RANK = {"low": 0, "medium": 1, "high": 2}
_SOURCE_PREFIX = {"audio": "speech", "text": "on-screen"}


def safety_detections(text: str, kind: str, score: float) -> list[Detection]:
    """Brand-safety hits in text as Detections.

    kind: "audio" (Whisper) -> detection kind "safety_speech"; "text" (OCR) -> "safety_ocr".
    Term "speech: killed" ends up in signals.safety; categories = [subtype] (+ "sensitive").
    Whether a window really becomes sensitive is decided in aggregate (severity rules).
    """
    source = _SOURCE_PREFIX.get(kind, kind)
    out: list[Detection] = []
    for words, st in match_safety(text):
        cats = [st.subtype] + (["sensitive"] if st.sensitive else [])
        out.append(Detection(
            term=f"{source}: {words}",
            score=score,
            kind="safety_speech" if kind == "audio" else "safety_ocr",
            category=st.subtype,
            categories=cats,
            severity=st.severity,
        ))
    return out


def news_detections(text: str, kind: str, score: float) -> list[Detection]:
    """News-report cues (category "news", signal "news")."""
    tokens = normalize(text).split()
    source = _SOURCE_PREFIX.get(kind, kind)
    out: list[Detection] = []
    for phrase in languages.active().news_markers:
        found = _find(tokens, phrase)
        if found:
            out.append(Detection(term=f"{source}: {found}", score=score, kind="news",
                                 category="news", categories=["news"]))
    return out
