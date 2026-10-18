@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-VoxCPM.ps1" %*
if errorlevel 1 goto failed
exit /b 0
:failed
echo VoxCPM setup failed. See the error above.
pause
exit /b 1
