@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Restore-GameResources.ps1" %*
if errorlevel 1 (
  echo Resource rebuild failed. See the error above.
  exit /b 1
)
exit /b 0
