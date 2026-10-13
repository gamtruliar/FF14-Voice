@echo off
setlocal
cd /d "%~dp0VoxCPM"
set "HF_HOME=%~dp0.cache\huggingface"
set "MODELSCOPE_CACHE=%~dp0.cache\modelscope"
set "GRADIO_ANALYTICS_ENABLED=False"
".venv\Scripts\python.exe" -u ff14_server.py %*
if errorlevel 1 pause
