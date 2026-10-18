@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-VoxCPM-GGUF.ps1" %*
if errorlevel 1 (
  echo Setup failed. Read the error above, then rerun to resume.
  pause
  exit /b 1
)
pause
