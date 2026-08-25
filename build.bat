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
"%MSBUILD%" "%~dp0main\OSTGUI.csproj" /t:Build /p:Configuration=Debug /m /nologo ^
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

set "APPEXE=%~dp0%OUTDIR%\OSTGUI.exe"

REM Known quirk: managed outputs (exe/dll) may land in a drive-stripped
REM copy of BaseOutputPath resolved against the project dir
REM (main\.build\...) while XBF/PRI go to the canonical repo-root .build.
REM If the canonical exe is missing, mirror from the project-local copy.
if exist "%APPEXE%" goto :verify_ok

set "ALTDIR=%~dp0main\.build\OSTGUI\bin\Debug\net10.0-windows10.0.19041.0\win-x64"
if not exist "%ALTDIR%\OSTGUI.exe" goto :verify_fail

echo [sync] canonical output missing, mirroring project-local build output
robocopy "%ALTDIR%" "%~dp0%OUTDIR%" /MIR /NFL /NDL /NJH /NJS >nul
if errorlevel 8 (
    echo [BUILD ERROR] robocopy failed syncing %ALTDIR%
    exit /b 1
)
goto :verify_ok

:verify_fail
echo [BUILD ERROR] output exe not found: %APPEXE%
echo If this persists, wipe .build\ and main\.build\ and retry once.
exit /b 1

:verify_ok
echo [2/2] [BUILD OK] %APPEXE%

if defined RUN_AFTER (
    echo Launching app...
    start "" "%APPEXE%"
)
exit /b 0
