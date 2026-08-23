@echo off
setlocal
cd /d "%~dp0"

REM Usage: build.bat [/r]    (/r = launch app after successful build)
REM Output: quiet console; full log at %LOG%; errors auto-printed on failure.

set "RUN_AFTER="
if /i "%~1"=="/r" set "RUN_AFTER=1"

set "LOGFILE=%TEMP%\ostgui_build.log"
set "ERRFILE=%TEMP%\ostgui_build_errors.log"
set "OUTDIR=OSTGUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
set "MSBUILD="

for %%V in (18 17) do (
    if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\%%V\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\%%V\Community\MSBuild\Current\Bin\MSBuild.exe"
    if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\%%V\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\%%V\Professional\MSBuild\Current\Bin\MSBuild.exe"
    if not defined MSBUILD if exist "C:\Program Files\Microsoft Visual Studio\%%V\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files\Microsoft Visual Studio\%%V\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
    if not defined MSBUILD if exist "C:\Program Files (x86)\Microsoft Visual Studio\%%V\Community\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\%%V\Community\MSBuild\Current\Bin\MSBuild.exe"
    if not defined MSBUILD if exist "C:\Program Files (x86)\Microsoft Visual Studio\%%V\Professional\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\%%V\Professional\MSBuild\Current\Bin\MSBuild.exe"
    if not defined MSBUILD if exist "C:\Program Files (x86)\Microsoft Visual Studio\%%V\Enterprise\MSBuild\Current\Bin\MSBuild.exe" set "MSBUILD=C:\Program Files (x86)\Microsoft Visual Studio\%%V\Enterprise\MSBuild\Current\Bin\MSBuild.exe"
)

if not defined MSBUILD (
    echo [ERROR] Visual Studio MSBuild not found. Install VS2022+ with WinUI workload.
    exit /b 1
)

echo [1/2] Building OSTGUI (Debug) ...
echo        full log: %LOGFILE%
"%MSBUILD%" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /m /nologo ^
  /v:q ^
  /flp:"LogFile=%LOGFILE%;Verbosity=normal" ^
  /flp1:"LogFile=%ERRFILE%;Errorsonly=true"
set "EC=%ERRORLEVEL%"

if not "%EC%"=="0" (
    echo.
    echo [BUILD FAILED] exit code %EC%
    echo ---------- errors ----------
    type "%ERRFILE%" 2>nul
    echo ----------------------------
    echo full log: %LOGFILE%
    exit /b %EC%
)

echo [2/2] [BUILD OK] %OUTDIR%\OSTGUI.exe

if defined RUN_AFTER (
    echo Launching app...
    start "" "%OUTDIR%\OSTGUI.exe"
)
exit /b 0
