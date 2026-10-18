@echo off
setlocal
cd /d "%~dp0"
if not exist "artifacts\ref-quality\minfilia-ja\clone-comparison.html" (
  echo Run the reference-quality pilot first. See README.md.
  pause
  exit /b 1
)
start "" "artifacts\ref-quality\minfilia-ja\clone-comparison.html"
