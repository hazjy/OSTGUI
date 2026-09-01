@echo off
setlocal
set "WIDTH=%~1"
if "%WIDTH%"=="" set "WIDTH=180"
taskkill /f /im OSTGUI.exe >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0set-sidebar-width.ps1" %WIDTH%
pause
endlocal
