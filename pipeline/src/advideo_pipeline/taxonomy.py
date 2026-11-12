"""Shared taxonomy: detected terms -> ad categories, CLIP labels, brand lexicon.

Single source of truth is ``schema/taxonomy.json`` at the repo root — the .NET ad
matcher (M2) reads the same file, so the category/brand vocabulary used in JSON
content maps always matches what advertiser campaigns can target.

Lookup order for the JSON file:
1. ``ADVIDEO_TAXONOMY`` environment variable (explicit path, e.g. in containers),
2. ``schema/taxonomy.json`` found by walking up from this module (repo / editable install).
"""

from __future__ import annotations

import json
import os
from pathlib import Path


def _locate() -> Path:
    env = os.environ.get("ADVIDEO_TAXONOMY")
    if env:
        return Path(env)
    for parent in Path(__file__).resolve().parents:
        candidate = parent / "schema" / "taxonomy.json"
        if candidate.is_file():
            return candidate
    raise FileNotFoundError(
        "schema/taxonomy.json not found; set ADVIDEO_TAXONOMY to its path"
    )


def _load() -> dict:
    with _locate().open(encoding="utf-8") as fh:
        return json.load(fh)


_DOC = _load()

CATEGORIES: list[str] = list(_DOC["categories"])

# --- object/term -> categories -----------------------------------------------
# Keys are lowercase detector labels (COCO class names for YOLO, CLIP labels).
TERM_CATEGORIES: dict[str, list[str]] = {
    k.lower(): list(v) for k, v in _DOC["termCategories"].items()
}

# --- CLIP zero-shot candidate labels -----------------------------------------
# (label, detection kind). Kind drives how it lands in the JSON map signals.
CLIP_SCENE_LABELS: list[tuple[str, str]] = [
    (e["label"], e["kind"]) for e in _DOC["clipSceneLabels"]
]
# distractor labels: scored (they absorb probability mass) but never emitted
CLIP_NEUTRAL_LABELS: frozenset[str] = frozenset(
    e["label"] for e in _DOC["clipSceneLabels"] if e.get("neutral")
)

# --- brand lexicon -----------------------------------------------------------
# alias (lowercase, may be multi-word) -> (canonical brand label, categories).
BRAND_LEXICON: dict[str, tuple[str, list[str]]] = {
    alias.lower(): (b["label"], list(b["categories"]))
    for b in _DOC["brands"]
    for alias in b["aliases"]
}


def categories_for(term: str) -> list[str]:
    return list(TERM_CATEGORIES.get(term.lower(), []))
