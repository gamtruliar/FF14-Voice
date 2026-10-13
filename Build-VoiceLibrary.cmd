@echo off
setlocal
cd /d "%~dp0"
set "SQPACK=D:\Game\ff14\SquareEnix\FINAL FANTASY XIV - A Realm Reborn\game\sqpack"
if not "%~1"=="" set "SQPACK=%~1"
if not exist "artifacts\game-voices\voice-clips.json" (
  dotnet run --project tools\FF14Sound.VoiceScan -- "%SQPACK%" "%~dp0artifacts\game-voices"
  if errorlevel 1 goto failed
)
dotnet run --project tools\FF14Sound.VoiceLibrary -- "%SQPACK%" "%~dp0artifacts\game-voices\voice-clips.json" "%~dp0data\game-voices"
if errorlevel 1 goto failed
echo Voice library ready. Start FF14Sound to use it.
exit /b 0
:failed
echo Voice library build failed. See the error above.
pause
exit /b 1
