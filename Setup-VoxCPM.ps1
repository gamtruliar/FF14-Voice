param([switch]$CpuOnly)
$ErrorActionPreference = 'Stop'
if (!(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'VoxCPM/uv.lock'))) {
    throw 'VoxCPM submodule is missing. Run: git submodule update --init --recursive'
}
if (!(Get-Command uv -ErrorAction SilentlyContinue)) { throw 'Install uv and reopen your terminal.' }
$env:UV_CACHE_DIR = Join-Path $PSScriptRoot '.cache/uv'
Push-Location (Join-Path $PSScriptRoot 'VoxCPM')
try {
    uv sync --frozen --extra dev
    if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
    uv pip install --python .venv/Scripts/python.exe -r (Join-Path $PSScriptRoot 'tools/gemini-requirements.txt')
    if ($LASTEXITCODE -ne 0) { throw 'Gemini SDK installation failed.' }
    if (!$CpuOnly) {
        uv pip install --python .venv/Scripts/python.exe torch==2.10.0+cu128 torchaudio==2.10.0+cu128 --index-url https://download.pytorch.org/whl/cu128
        if ($LASTEXITCODE -ne 0) { throw 'CUDA PyTorch installation failed.' }
    }
} finally { Pop-Location }
