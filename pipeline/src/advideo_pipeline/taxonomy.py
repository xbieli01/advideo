"""Shared taxonomy: detected terms -> ad categories, and CLIP candidate labels.

Both the mock and the real (YOLO+CLIP) backends map their raw detections through
this module so the JSON content map uses one consistent category vocabulary that
the M2 matching engine can target with advertiser campaigns.
"""

from __future__ import annotations

# --- object/term -> categories -----------------------------------------------
# Keys are lowercase detector labels (COCO class names for YOLO, CLIP labels).
TERM_CATEGORIES: dict[str, list[str]] = {
    # automotive
    "car": ["automotive"],
    "truck": ["automotive"],
    "bus": ["automotive", "transport"],
    "motorcycle": ["automotive"],
    "road": ["automotive"],
    "driving": ["automotive"],
    "traffic": ["automotive", "transport"],
    # sport / footwear
    "running": ["sport"],
    "sneakers": ["footwear", "sport"],
    "sports ball": ["sport"],
    "skateboard": ["sport"],
    "surfboard": ["sport"],
    "tennis racket": ["sport"],
    "bicycle": ["sport", "transport"],
    "gym": ["sport", "fitness"],
    # tech
    "laptop": ["tech"],
    "cell phone": ["tech"],
    "keyboard": ["tech"],
    "tv": ["tech", "electronics"],
    # food & drink
    "pizza": ["food"],
    "cup": ["food", "beverage"],
    "wine glass": ["beverage"],
    "bottle": ["beverage"],
    # travel / outdoor
    "airplane": ["travel", "transport"],
    "boat": ["travel"],
    "beach": ["travel", "outdoor"],
    "mountain": ["outdoor", "travel"],
    # fashion / people
    "handbag": ["fashion"],
    "tie": ["fashion"],
    "person": [],  # neutral; presence signal only
}

# --- CLIP zero-shot candidate labels -----------------------------------------
# (label, detection kind). Kind drives how it lands in the JSON map signals.
CLIP_SCENE_LABELS: list[tuple[str, str]] = [
    ("driving", "action"),
    ("running", "action"),
    ("cooking", "action"),
    ("dancing", "action"),
    ("shopping", "action"),
    ("gaming", "action"),
    ("outdoor", "scene"),
    ("indoor", "scene"),
    ("beach", "scene"),
    ("city street", "scene"),
    ("mountain", "scene"),
    ("office", "scene"),
    ("gym", "scene"),
]


def categories_for(term: str) -> list[str]:
    return list(TERM_CATEGORIES.get(term.lower(), []))


# --- brand lexicon -----------------------------------------------------------
# alias (lowercase, may be multi-word) -> (canonical brand label, categories).
# Used by OCR (on-screen text) and Whisper (spoken) backends to turn recognized
# text into brand *entities* — the core of the product's brand-match use case
# (a Tesla shot -> Tesla ad if Tesla is a customer).
BRAND_LEXICON: dict[str, tuple[str, list[str]]] = {
    "tesla": ("Tesla", ["automotive"]),
    "volkswagen": ("VW", ["automotive"]),
    "vw": ("VW", ["automotive"]),
    "audi": ("Audi", ["automotive"]),
    "bmw": ("BMW", ["automotive"]),
    "mercedes": ("Mercedes-Benz", ["automotive"]),
    "toyota": ("Toyota", ["automotive"]),
    "nike": ("Nike", ["sport", "footwear"]),
    "adidas": ("Adidas", ["sport", "footwear"]),
    "puma": ("Puma", ["sport", "footwear"]),
    "coca cola": ("Coca-Cola", ["beverage"]),
    "coca-cola": ("Coca-Cola", ["beverage"]),
    "pepsi": ("Pepsi", ["beverage"]),
    "red bull": ("Red Bull", ["beverage"]),
    "apple": ("Apple", ["tech"]),
    "samsung": ("Samsung", ["tech", "electronics"]),
    "google": ("Google", ["tech"]),
    "microsoft": ("Microsoft", ["tech"]),
}
