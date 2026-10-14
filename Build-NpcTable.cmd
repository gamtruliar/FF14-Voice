@echo off
setlocal
cd /d "%~dp0"
set "SQPACK=D:\Game\ff14\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
if not "%~1"=="" set "SQPACK=%~1"
dotnet run --project tools\FF14Sound.VoiceLibrary -- --npc-table "%SQPACK%" "%~dp0data\game-voices"
if errorlevel 1 pause
