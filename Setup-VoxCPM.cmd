@echo off
setlocal
cd /d "%~dp0VoxCPM"
set "UV_CACHE_DIR=%~dp0.cache\uv"
uv sync --frozen --extra dev
if errorlevel 1 goto failed
uv pip install --python .venv/Scripts/python.exe torch==2.10.0+cu128 torchaudio==2.10.0+cu128 --index-url https://download.pytorch.org/whl/cu128
if errorlevel 1 goto failed
exit /b 0
:failed
echo VoxCPM setup failed. See the error above.
pause
exit /b 1
