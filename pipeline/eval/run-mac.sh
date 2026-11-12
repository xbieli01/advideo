#!/usr/bin/env bash
# Run the analyzer eval on a Mac (Apple Silicon -> MPS) or any machine with Python 3.10+ and ffmpeg.
# Usage: pipeline/eval/run-mac.sh [extra run_eval.py args]     (from the repo root)
set -euo pipefail
cd "$(dirname "$0")/.."
command -v ffmpeg >/dev/null || { echo "ffmpeg missing: brew install ffmpeg"; exit 2; }
[ -d .venv-ml ] || python3 -m venv .venv-ml
.venv-ml/bin/pip install -q --upgrade pip
.venv-ml/bin/pip install -q -e ".[ml,dev]"
.venv-ml/bin/python eval/fetch.py
mkdir -p eval/results
.venv-ml/bin/python eval/run_eval.py --config yolo+clip --report eval/results/report.md "$@"
.venv-ml/bin/python eval/run_eval.py --config "yolo+clip,ocr+whisper" --report eval/results/report.md "$@"
echo "report: pipeline/eval/results/report.md"
