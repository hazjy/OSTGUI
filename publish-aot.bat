@echo off
rem ============================================================
rem  Native AOT publish (self-contained + unpackaged)
rem  Output: .build\OSTGUI\publish-aot\
rem  Flags below are the ones validated on this machine:
rem    - PublishAot=true        real AOT (no coreclr.dll, no OSTGUI.dll)
rem    - PublishSingleFile=false  WinUI 3 does not support single-file
rem    - PublishTrimmed must NOT be set: AOT implies trimming and
rem      passing PublishTrimmed=false makes MSBuild fail
rem  See docs\dev\REF-AOT适配.md for the six pitfalls we hit.
rem ============================================================
setlocal
set "MSB=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
set "PROJ=%~dp0main\OSTGUI.csproj"
set "OUT=%~dp0.build\OSTGUI\publish-aot\"

if not exist "%MSB%" (
  echo [ERROR] MSBuild not found: %MSB%
  exit /b 1
)

"%MSB%" "%PROJ%" /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:PublishSingleFile=false /p:PublishReadyToRun=false /p:CsWinRTAotWarningLevel=2 /p:PublishDir="%OUT%"
set "CODE=%ERRORLEVEL%"

echo.
if exist "%OUT%OSTGUI.pdb" del /q "%OUT%OSTGUI.pdb"
if "%CODE%"=="0" (
  echo [OK] exit=%CODE%
  echo      output: %OUT%
) else (
  echo [FAIL] exit=%CODE%
)
exit /b %CODE%
