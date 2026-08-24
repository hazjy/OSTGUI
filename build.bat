@echo off
setlocal
cd /d "%~dp0"

REM Usage: build.bat [/r]    (/r = launch app after successful build)
REM Output: quiet console; full log at %LOG%; errors auto-printed on failure.
REM Note: WindowsAppSDK self-contained copy leaves a redundant nested build
REM dir under main\ on every build; it is removed after a successful build
REM so the built app lives only in OUTDIR below.

set "RUN_AFTER="
if /i "%~1"=="/r" set "RUN_AFTER=1"

set "LOGFILE=%TEMP%\ostgui_build.log"
set "ERRFILE=%TEMP%\ostgui_build_errors.log"
set "OUTDIR=main\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
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
"%MSBUILD%" main\OSTGUI.csproj /t:Build /p:Configuration=Debug /m /nologo ^
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

REM Remove redundant nested build copies produced by WindowsAppSDK
REM self-contained mode. The nested subdir name varies with the project
REM dir name (e.g. main\OSTGUI\bin or main\main\bin), so match any subdir
REM of main\ that contains a full build output.
for /d %%D in ("%~dp0main\*") do (
    if exist "%%D\bin\Debug\net10.0-windows10.0.19041.0\win-x64\OSTGUI.exe" (
        echo [cleanup] removing redundant nested output: %%D
        rd /s /q "%%D"
    )
)

set "APPEXE=%~dp0%OUTDIR%\OSTGUI.exe"
if not exist "%APPEXE%" (
    echo [BUILD ERROR] output exe not found: %APPEXE%
    exit /b 1
)

echo [2/2] [BUILD OK] %APPEXE%

if defined RUN_AFTER (
    echo Launching app...
    start "" "%APPEXE%"
)
exit /b 0