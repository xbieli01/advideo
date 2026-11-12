"""Torch device selection shared by the model backends: ADVIDEO_DEVICE env > CUDA > Apple MPS > CPU."""

from __future__ import annotations

import os


def pick_device(explicit: str | None = None) -> str:
    choice = explicit or os.environ.get("ADVIDEO_DEVICE")
    if choice:
        return choice
    import torch  # lazy

    if torch.cuda.is_available():
        return "cuda"
    mps = getattr(torch.backends, "mps", None)
    if mps is not None and mps.is_available():
        return "mps"
    return "cpu"
