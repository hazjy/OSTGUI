@echo off
cd /d %~dp0
echo ============================================
echo   OSTGUI Build + Run Diagnostic Script
echo ============================================
echo.

call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 >nul

echo [1/3] Building project...
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /v:m
if errorlevel 1 (
    echo.
    echo [ERROR] Build FAILED. Check errors above.
    pause
    exit /b 1
)
echo [BUILD OK]
echo.

echo [2/3] Starting app...
set "APP=OSTGUI\bin\Debug\net8.0-windows10.0.19041.0\win-x64\OSTGUI.exe"
"%APP%"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
echo ============================================
echo   App exited, EXIT_CODE: %EXIT_CODE%
echo ============================================
echo.
echo Exit code meanings:
echo   0              = Normal exit
echo   -1073741515    = 0xC0000135 DLL not found
echo   -1073740286    = 0xC0000409 Unhandled exception crash
echo   -1073741819    = 0xC0000005 Access violation
echo   3221225473     = 0xC0000001
echo.
echo Please copy the EXIT_CODE value and send it back.
echo.
pause