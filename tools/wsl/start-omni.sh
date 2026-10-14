#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT="${FF14_OMNI_ROOT:-$HOME/.local/share/ff14sound-omni}"
mkdir -p "$TASK_ROOT"
exec 9>"$TASK_ROOT/server.lock"
flock -n 9 || { echo 'FF14Sound Omni is already starting/running.'; exit 0; }
PROJECT_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$PROJECT_ROOT/artifacts"
exec > >(tee -a "$PROJECT_ROOT/artifacts/omni-server.log") 2>&1
export PATH="$TASK_ROOT/venv/bin:$PATH"
export VLLM_WORKER_MULTIPROC_METHOD=spawn
export HF_HOME="$PROJECT_ROOT/.cache/huggingface"
export HF_HUB_OFFLINE=1
export HF_HUB_DISABLE_TELEMETRY=1
export VLLM_NO_USAGE_STATS=1
export VLLM_LOGGING_LEVEL=WARNING
export TOKENIZERS_PARALLELISM=false
export OMP_NUM_THREADS=4
export TORCHINDUCTOR_CACHE_DIR="$TASK_ROOT/inductor-cache"
MODEL="$PROJECT_ROOT/.cache/huggingface/hub/models--openbmb--VoxCPM2/snapshots/32279effe8c19989596f05d353d1447f51d9e915"
test -f "$MODEL/model.safetensors"
printf '%s\n' "$$" > "$TASK_ROOT/server.pid"
exec "$TASK_ROOT/venv/bin/vllm" serve "$MODEL" --omni \
  --served-model-name voxcpm2 --host 127.0.0.1 --port 8809 \
  --deploy-config "$PROJECT_ROOT/tools/wsl/voxcpm2-3090.yaml"
