@echo off
setlocal
cd /d "%~dp0"

REM Usage: build.bat [/r]    (/r = launch app after successful build)
REM Output: quiet console; full log at %LOGFILE%; errors auto-printed on failure.
REM Note: ALL build outputs live under the repo-level .build\ directory
REM (see Directory.Build.props), outside the project source trees.

set "RUN_AFTER="
if /i "%~1"=="/r" set "RUN_AFTER=1"

set "LOGFILE=%TEMP%\ostgui_build.log"
set "ERRFILE=%TEMP%\ostgui_build_errors.log"
set "OUTDIR=.build\OSTGUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
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
REM Pass an ABSOLUTE project path: with a relative one, custom
REM BaseOutputPath (from Directory.Build.props) gets re-resolved against
REM the current directory in some child evaluations and outputs land in
REM main\.build\ instead of the canonical repo-root .build\.
"%MSBUILD%" "%~dp0main\OSTGUI.csproj" /t:Build /restore /p:Configuration=Debug /m /nologo ^
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

REM WindowsAppSDK quirk: every Debug build puts the fresh managed outputs in
REM a nested copy (main\.build\... & NoSteamLauncher\.build\...) while the
REM canonical repo-root .build may keep stale files. So BUILD OK on a stale
REM canonical exe was silently shipping old builds. Sync anything fresher from
REM the nested copies back to canonical BEFORE verifying. Idempotent: /E /XO
REM overwrites only when the source file is newer; never deletes.
set "CANON=%~dp0%OUTDIR%"
set "ALTMAIN=%~dp0main\.build\OSTGUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
set "ALTNS=%~dp0NoSteamLauncher\.build\NoSteamLauncher\bin\Debug\net8.0"
if exist "%ALTMAIN%\OSTGUI.exe" robocopy "%ALTMAIN%" "%CANON%" /E /XO /NFL /NDL /NJH /NJS >nul
if exist "%ALTNS%\NoSteamLauncher.dll" robocopy "%ALTNS%" "%~dp0.build\NoSteamLauncher\bin\Debug\net8.0" /E /XO /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
    echo [BUILD ERROR] robocopy sync failed
    exit /b 1
)

set "APPEXE=%CANON%\OSTGUI.exe"
if not exist "%APPEXE%" (
    echo [BUILD ERROR] output exe not found: %APPEXE%
    echo If this persists, wipe .build\ and main\.build\ and retry once.
    exit /b 1
)

REM Nested copies are redundant now -- drop them so they never linger.
rd /s /q "%~dp0main\.build" 2>nul
rd /s /q "%~dp0NoSteamLauncher\.build" 2>nul
echo [2/2] [BUILD OK] %APPEXE%

if defined RUN_AFTER (
    echo Launching app...
    start "" "%APPEXE%"
)
exit /b 0
