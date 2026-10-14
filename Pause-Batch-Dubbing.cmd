@echo off
setlocal
cd /d "%~dp0"
if not exist "data\batch-ja" mkdir "data\batch-ja"
echo pause>"data\batch-ja\pause.request"
echo Pause requested. The active sentence will finish and be saved.
