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
