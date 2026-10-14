#!/usr/bin/env bash
set -euo pipefail
TASK_ROOT="${FF14_OMNI_ROOT:-$HOME/.local/share/ff14sound-omni}"
if [[ ! -f "$TASK_ROOT/server.pid" ]]; then exit 0; fi
read -r task_pid < "$TASK_ROOT/server.pid"
[[ "$task_pid" =~ ^[0-9]+$ ]] || { echo 'Invalid Omni PID'; exit 1; }
command_line="$(ps -p "$task_pid" -o args= || true)"
if [[ -z "$command_line" ]]; then exit 0; fi
if [[ "$command_line" != *"$TASK_ROOT/venv/bin/vllm serve "* || "$command_line" != *"--port 8809"* ]]; then
    echo 'PID does not belong to the FF14Sound Omni server; not stopping it.' >&2
    exit 1
fi
kill -TERM "$task_pid"
