$ErrorActionPreference = 'Stop'
$env:UV_CACHE_DIR = Join-Path $PSScriptRoot '.cache/uv'
$python = Join-Path $PSScriptRoot 'VoxCPM/.venv/Scripts/python.exe'
if (!(Test-Path -LiteralPath $python)) { throw 'Run Setup-VoxCPM.cmd first.' }
uv pip install --python $python -r (Join-Path $PSScriptRoot 'tools/gemini-requirements.txt')
if ($LASTEXITCODE -ne 0) { throw 'Gemini SDK installation failed. Close the local API gateway and retry if a dependency is locked.' }
Write-Host 'google-genai installed. Restart the local API gateway to load the update.'
