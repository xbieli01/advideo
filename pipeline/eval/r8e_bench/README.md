# R8e classifier benchmark (DEV broadcasts only)

Scratch tooling used to compare text-safety classifiers on story chunks (2026-11-24). Paths are hard-wired to the
sandbox layout (~/eval/r8d/tr-small transcripts, ~/eval/r8e-b cache). Order: `chunks.py 24 8` →
`score.py <cand> 24_8` (resumable; cand = minilm_lr | e5s | e5b | nli | qwen05) → `evalc.py <cand> 24_8 score|lr`
(grid), `lobo.py` (leave-one-broadcast-out), `fixed.py` (fixed thresholds per broadcast).
Never run on the holdout broadcasts.
