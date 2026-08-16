@echo off
cd /d %~dp0
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 >nul

echo Building...
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /m /v:m
set "EXIT_CODE=%ERRORLEVEL%"

if %EXIT_CODE% neq 0 (
    echo.
    echo ============================================
    echo   Build FAILED (EXIT_CODE: %EXIT_CODE%)
    echo ============================================
    echo.
    echo Common MSBuild exit codes:
    echo   1   = Build failed (compile errors, missing refs, etc.)
    echo   -1  = MSBuild itself crashed / invalid args
    echo.
    echo Check the compiler output above for specific errors.
    echo.
    exit /b %EXIT_CODE%
)

echo Build succeeded.