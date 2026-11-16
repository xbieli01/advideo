"""Process-wide cache of loaded open_clip models, so several analyzers (scene labels, brand logos)
share one copy of the weights."""

from __future__ import annotations

from functools import lru_cache


@lru_cache(maxsize=4)
def load_clip(model: str, pretrained: str, device: str):
    import open_clip  # lazy

    clip, _, preprocess = open_clip.create_model_and_transforms(model, pretrained=pretrained, device=device)
    clip.eval()
    tokenizer = open_clip.get_tokenizer(model)
    logit_scale = float(clip.logit_scale.exp().item())
    return clip, preprocess, tokenizer, logit_scale
