@echo off
setlocal
cd /d "%~dp0"
dotnet build "src\FF14Sound\FF14Sound.csproj" --configuration Release --nologo
if errorlevel 1 goto failed
start "" "%~dp0src\FF14Sound\bin\Release\net10.0-windows\FF14Sound.exe" --batch-window
exit /b 0
:failed
pause
