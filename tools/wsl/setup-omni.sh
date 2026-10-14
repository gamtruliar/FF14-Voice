#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT="${FF14_OMNI_ROOT:-$HOME/.local/share/ff14sound-omni}"
mkdir -p "$TASK_ROOT"
python3 -m venv "$TASK_ROOT/venv"
"$TASK_ROOT/venv/bin/python" -m pip install 'uv==0.11.13'
export UV_CACHE_DIR="$TASK_ROOT/uv-cache"
"$TASK_ROOT/venv/bin/uv" pip install --python "$TASK_ROOT/venv/bin/python" \
  'vllm==0.30.0' 'vllm-omni==0.30.0' 'voxcpm==2.0.3' soundfile httpx ninja
"$TASK_ROOT/venv/bin/uv" pip freeze --python "$TASK_ROOT/venv/bin/python" > "$TASK_ROOT/requirements-installed.txt"
"$TASK_ROOT/venv/bin/python" -c 'import torch, importlib.metadata as m; print({p:m.version(p) for p in ("torch","vllm","vllm-omni","voxcpm")}); print(torch.cuda.get_device_name(0)); print(torch.cuda.get_device_capability(0))'
