@echo off
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Restore-GameResources.ps1" -Task MatchNames %*
exit /b %errorlevel%
