@echo off
setlocal
cd /d "%~dp0"
dotnet build "src\FF14Sound\FF14Sound.csproj" --configuration Release --nologo
if errorlevel 1 goto failed
set "FF14SOUND_EXE=%~dp0src\FF14Sound\bin\Release\net10.0-windows\FF14Sound.exe"
powershell.exe -NoProfile -Command "try { Start-Process -FilePath $env:FF14SOUND_EXE -Verb RunAs -WorkingDirectory (Split-Path -Parent $env:FF14SOUND_EXE) -ErrorAction Stop } catch { Write-Error $_; exit 1 }"
if errorlevel 1 goto failed
exit /b 0
:failed
pause
exit /b 1
