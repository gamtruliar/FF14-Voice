param([string]$Device = 'auto', [int]$Port = 8808)
$ErrorActionPreference = 'Stop'
$env:HF_HOME = Join-Path $PSScriptRoot '.cache/huggingface'
$env:MODELSCOPE_CACHE = Join-Path $PSScriptRoot '.cache/modelscope'
$env:GRADIO_ANALYTICS_ENABLED = 'False'
$pythonPath = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $pythonPath)) { throw 'Run Setup-VoxCPM.ps1 first.' }
Push-Location (Join-Path $PSScriptRoot 'VoxCPM')
try { & $pythonPath ff14_server.py --device $Device --port $Port }
finally { Pop-Location }
