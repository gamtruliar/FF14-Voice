@echo off
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-FF14Voice.ps1" %*
if errorlevel 1 (
  echo Installation stopped. Read the error above, fix it, and run this file again.
  pause
  exit /b 1
)
pause
