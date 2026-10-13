@echo off
setlocal
cd /d "%~dp0"
dotnet run --project "src\FF14Sound\FF14Sound.csproj"
if errorlevel 1 pause
