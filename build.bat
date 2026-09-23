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
REM Pass an ABSOLUTE project path: 相对路径下 MSBuild 可能把自定义 BaseOutputPath
REM 重新解析到当前目录、产物落到 <项目>\.build\（根因已在 Directory.Build.props
REM 改成绝对路径，这里保持绝对以免再触发；兜底见文件末尾的嵌套目录清理）。
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

REM 兜底：内层构建偶发把**相对**的输出基路径重定位到项目目录，于是冒出 <项目>\.build\ 这种
REM 嵌套副本（根因见 Directory.Build.props 的绝对路径；这里防别的项目/别的 SDK 再踩进来）。
REM 嵌套副本可能比 canonical 还新，所以先同步回去再删。idempotent：/E /XO 只在源更新时覆盖。
set "CANON=%~dp0%OUTDIR%"
for /d %%P in ("%~dp0*") do (
    if exist "%%P\.build" if /i not "%%~nxP"==".build" (
        robocopy "%%P\.build" "%~dp0.build" /E /XO /NFL /NDL /NJH /NJS >nul
        if errorlevel 8 (
            echo [BUILD ERROR] robocopy sync failed: %%P\.build
            exit /b 1
        )
        rd /s /q "%%P\.build"
    )
)

set "APPEXE=%CANON%\OSTGUI.exe"
if not exist "%APPEXE%" (
    echo [BUILD ERROR] output exe not found: %APPEXE%
    echo If this persists, wipe .build\ and retry once.
    exit /b 1
)

echo [2/2] [BUILD OK] %APPEXE%

if defined RUN_AFTER (
    echo Launching app...
    start "" "%APPEXE%"
)
exit /b 0
