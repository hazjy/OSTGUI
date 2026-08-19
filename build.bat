@echo off
cd /d %~dp0

REM 检测 OSTGUI 是否运行，如果运行则关闭
tasklist /FI "IMAGENAME eq OSTGUI.exe" 2>NUL | find /I "OSTGUI.exe" >NUL
if %ERRORLEVEL% == 0 (
    echo Closing running OSTGUI...
    taskkill /F /IM OSTGUI.exe >NUL 2>&1
    timeout /t 2 /nobreak >nul
)

REM 检测 VS 路径（优先 2022，回退 2019）
set "MSBuildPath="
if exist "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBuildPath=C:\Program Files\Microsoft Visual Studio\18\Community"
) else if exist "C:\Program Files\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBuildPath=C:\Program Files\Microsoft Visual Studio\17\Community"
) else if exist "C:\Program Files (x86)\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBuildPath=C:\Program Files (x86)\Microsoft Visual Studio\18\Community"
) else if exist "C:\Program Files (x86)\Microsoft Visual Studio\17\Community\MSBuild\Current\Bin\MSBuild.exe" (
    set "MSBuildPath=C:\Program Files (x86)\Microsoft Visual Studio\17\Community"
)

if "%MSBuildPath%"=="" (
    echo ERROR: Visual Studio not found!
    echo Please install Visual Studio 2022 or 2019 with .NET desktop development workload.
    exit /b 1
)

REM 初始化 VS 环境
call "%MSBuildPath%\Common7\Tools\VsDevCmd.bat" -arch=x64 >nul

echo Building...
"%MSBuildPath%\MSBuild\Current\Bin\MSBuild.exe" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /m /v:m
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
