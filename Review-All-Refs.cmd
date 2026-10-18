@echo off
setlocal
cd /d "%~dp0"
if not exist "artifacts\ref-quality\all\index.html" (
  echo Reference quality batch is not complete yet.
  if exist "artifacts\ref-quality\all\status.json" type "artifacts\ref-quality\all\status.json"
  pause
  exit /b 1
)
start "" "artifacts\ref-quality\all\index.html"
