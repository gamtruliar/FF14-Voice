@echo off
setlocal
cd /d "%~dp0"
echo Restore the reference catalog saved before the full-library quality pass.
echo Current catalog must still match that pass; newer edits will not be overwritten.
choice /C YN /N /M "Restore? [Y/N] "
if errorlevel 2 exit /b 0
artifacts\ref-quality\.venv\Scripts\python.exe tools\refresh_voice_refs.py --restore
if errorlevel 1 goto failed
echo Restored. Reload the voice library in FF14 Voice.
pause
exit /b 0
:failed
pause
exit /b 1
