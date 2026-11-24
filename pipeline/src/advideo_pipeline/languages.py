"""Language packs: per-language lexicons and model settings (``schema/languages/<code>.json``).

A pack holds everything language-specific the pipeline needs:

* ``safetyTerms`` – brand-safety lexicon (``groups`` of subtype/severity terms + ``exclusions`` idioms);
  subtype/severity/sensitive definitions come from ``taxonomy.json`` ``safetyTerms.groups``
* ``newsMarkers`` – cues that a segment is a news report
* ``keywords`` – local words -> taxonomy terms (``"auto": "car"``) so speech/OCR in that language hits ad keywords
* ``semanticSafety`` – example sentences for the embedding classifier (packs without them fall back to ``en``)
* ``whisper`` / ``ocr`` – language codes for faster-whisper and EasyOCR

Which packs are active
----------------------
``ADVIDEO_LANGUAGES`` (comma list, ``all`` = every pack file) > ``taxonomy.json`` ``languages.enabled``.
Lexicons of all active packs are merged: a Slovak broadcast is matched only against e.g. ``sk,cs,en``, so an
English word that is an everyday word elsewhere ("rifle" = jeans in Slovak) does not fire when ``en`` is off.

``ADVIDEO_WHISPER_LANGUAGE`` – Whisper language (``auto`` = detect); default: the pack's code when exactly one
pack is active, else auto-detect. ``ADVIDEO_OCR_LANGUAGES`` – EasyOCR codes; default: union of active packs.

Add a language: drop ``schema/languages/<code>.json`` next to the others and enable it — no code change.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from functools import lru_cache
from pathlib import Path

from .taxonomy import SAFETY_GROUPS, TAXONOMY_PATH, TAXONOMY

__all__ = [
    "SafetyTerm",
    "LanguagePack",
    "LanguageSet",
    "languages_dir",
    "available_languages",
    "configured_languages",
    "load_pack",
    "active",
    "set_languages",
    "whisper_language",
    "ocr_languages",
]


@dataclass(frozen=True)
class SafetyTerm:
    phrase: str          # as written in the pack (may contain * prefix tokens)
    subtype: str         # violence | death | war | terror | disaster | accident | weapons | drugs | profanity
    severity: str        # high | medium | low
    sensitive: bool      # adds the "sensitive" category (blocked by the standard preset)
    language: str = ""


@dataclass(frozen=True)
class LanguagePack:
    code: str
    name: str
    whisper: str | None
    ocr: str | None
    safety_terms: tuple[SafetyTerm, ...]
    exclusions: tuple[str, ...]
    news_markers: tuple[str, ...]
    keywords: dict[str, str] = field(default_factory=dict)
    semantic: dict = field(default_factory=dict)


@dataclass(frozen=True)
class LanguageSet:
    """Merged view of the active packs (what textmatch / semantic safety use)."""

    codes: tuple[str, ...]
    packs: tuple[LanguagePack, ...]
    safety_terms: tuple[SafetyTerm, ...]
    exclusions: tuple[str, ...]
    news_markers: tuple[str, ...]
    keywords: dict[str, str]
    semantic_sensitive: dict[str, list[str]]
    semantic_neutral: list[str]


def languages_dir() -> Path:
    env = os.environ.get("ADVIDEO_LANGUAGES_DIR")
    if env:
        return Path(env)
    return TAXONOMY_PATH.parent / TAXONOMY.get("languages", {}).get("dir", "languages")


def available_languages() -> list[str]:
    d = languages_dir()
    return sorted(p.stem for p in d.glob("*.json")) if d.is_dir() else []


def configured_languages() -> list[str]:
    raw = os.environ.get("ADVIDEO_LANGUAGES", "").strip()
    if raw.lower() == "all":
        return available_languages()
    if raw:
        return [c.strip().lower() for c in raw.split(",") if c.strip()]
    return list(TAXONOMY.get("languages", {}).get("enabled", ["en"]))


@lru_cache(maxsize=None)
def load_pack(code: str) -> LanguagePack:
    path = languages_dir() / f"{code}.json"
    if not path.is_file():
        raise ValueError(f"unknown language '{code}': no pack {path} (available: {', '.join(available_languages())})")
    doc = json.loads(path.read_text(encoding="utf-8"))
    st = doc.get("safetyTerms", {})
    terms = []
    for g in st.get("groups", []):
        key = (g["subtype"], g["severity"])
        meta = SAFETY_GROUPS.get(key)
        if meta is None:
            raise ValueError(f"{path.name}: group {key} is not defined in taxonomy.json safetyTerms.groups")
        terms += [SafetyTerm(t, key[0], key[1], meta, code) for t in g.get("terms", [])]
    return LanguagePack(
        code=code,
        name=doc.get("name", code),
        whisper=doc.get("whisper", code),
        ocr=doc.get("ocr", code),
        safety_terms=tuple(terms),
        exclusions=tuple(st.get("exclusions", [])),
        news_markers=tuple(doc.get("newsMarkers", [])),
        keywords={k.lower(): v.lower() for k, v in doc.get("keywords", {}).items()},
        semantic=dict(doc.get("semanticSafety", {})),
    )


def _dedupe(items) -> tuple:
    return tuple(dict.fromkeys(items))


@lru_cache(maxsize=None)
def _build(codes: tuple[str, ...]) -> LanguageSet:
    if not codes:
        raise ValueError("no languages enabled (ADVIDEO_LANGUAGES / taxonomy languages.enabled)")
    packs = tuple(load_pack(c) for c in codes)
    # the same phrase in two packs (sk + cs share many words) is one term
    seen: dict[tuple[str, str, str], SafetyTerm] = {}
    for p in packs:
        for t in p.safety_terms:
            seen.setdefault((t.phrase, t.subtype, t.severity), t)
    keywords: dict[str, str] = {}
    for p in packs:
        keywords.update(p.keywords)
    sem_packs = [p for p in packs if p.semantic.get("sensitive")]
    if not sem_packs and "en" not in codes:
        try:
            sem_packs = [load_pack("en")]  # embeddings are multilingual: English prototypes still work
        except ValueError:
            sem_packs = []
    sensitive: dict[str, list[str]] = {}
    neutral: list[str] = []
    for p in sem_packs:
        for sub, ex in p.semantic.get("sensitive", {}).items():
            sensitive.setdefault(sub, []).extend(ex)
        neutral += p.semantic.get("neutral", [])
    return LanguageSet(
        codes=codes,
        packs=packs,
        safety_terms=tuple(seen.values()),
        exclusions=_dedupe(e for p in packs for e in p.exclusions),
        news_markers=_dedupe(n for p in packs for n in p.news_markers),
        keywords=keywords,
        semantic_sensitive=sensitive,
        semantic_neutral=neutral,
    )


_override: tuple[str, ...] | None = None


def active() -> LanguageSet:
    """Packs in effect now (``set_languages`` override, else env / taxonomy)."""
    return _build(_override if _override is not None else tuple(dict.fromkeys(configured_languages())))


def set_languages(codes: list[str] | tuple[str, ...] | str | None) -> LanguageSet:
    """Switch the active packs at runtime (``None`` = back to env/taxonomy). Returns the new set."""
    global _override
    if isinstance(codes, str):
        codes = [c.strip() for c in codes.split(",") if c.strip()]
    _override = tuple(dict.fromkeys(c.lower() for c in codes)) if codes is not None else None
    return active()


def whisper_language() -> str | None:
    """Language forced on Whisper, or None for auto-detection."""
    env = os.environ.get("ADVIDEO_WHISPER_LANGUAGE", "").strip().lower()
    if env:
        return None if env == "auto" else env
    packs = active().packs
    return packs[0].whisper if len(packs) == 1 else None


def ocr_languages() -> list[str]:
    """EasyOCR language codes for the active packs (``en`` always included: digits, brand names, UI text)."""
    env = os.environ.get("ADVIDEO_OCR_LANGUAGES", "").strip()
    if env:
        return [c.strip() for c in env.split(",") if c.strip()]
    codes = [p.ocr for p in active().packs if p.ocr]
    return list(dict.fromkeys(["en", *codes]))
