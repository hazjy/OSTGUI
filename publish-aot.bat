@echo off
rem ============================================================
rem  Native AOT publish (self-contained + unpackaged), plus the
rem  companion OnlineHost.exe.
rem  Output: .build\OSTGUI\publish-aot\
rem
rem  Flags validated on this machine:
rem    - PublishAot=true          real AOT (no coreclr.dll, no OSTGUI.dll)
rem    - PublishSingleFile=false  WinUI 3 does not support single-file
rem    - PublishTrimmed must NOT be set: AOT implies trimming and
rem      passing PublishTrimmed=false makes MSBuild fail
rem  See docs\dev\REF-AOT适配.md for the six pitfalls we hit.
rem
rem  NOTE: OUT deliberately has NO trailing backslash. With one, the
rem  closing quote in /p:PublishDir="%OUT%\" gets escaped and MSBuild
rem  fails with MSB4184 "illegal characters in path".
rem ============================================================
setlocal
set "MSB=C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
set "PROJ=%~dp0main\OSTGUI.csproj"
set "HOSTPROJ=%~dp0OnlineHost\OnlineHost.csproj"
set "OUT=%~dp0.build\OSTGUI\publish-aot"

if not exist "%MSB%" (
  echo [ERROR] MSBuild not found: %MSB%
  exit /b 1
)

rem ---- 0) restore with the SAME properties -----------------------------
rem Restore and Publish must be SEPARATE invocations. /t:Restore;Publish in one run
rem evaluates the project before the restore has written its generated props/targets,
rem and the WinUI XAML step then does not run at all (~98 CS0103 "InitializeComponent/
rem name does not exist"). Seen 2026-09-30.
rem The restore must also carry PublishAot/SelfContained/RuntimeIdentifier: without them
rem project.assets.json has no AOT runtime pack, MSBuild does not re-restore during
rem publish, and PublishAot is silently ignored -> a ~190 MB JIT build. Also 2026-09-30.
"%MSB%" "%PROJ%" /t:Restore /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:PublishSingleFile=false /p:PublishReadyToRun=false /p:CsWinRTAotWarningLevel=2 /nologo /v:m
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" goto fail

rem ---- 1) the GUI ----------------------------------------------------
"%MSB%" "%PROJ%" /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:PublishSingleFile=false /p:PublishReadyToRun=false /p:CsWinRTAotWarningLevel=2 /p:PublishDir="%OUT%" /nologo /v:m
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" goto fail

rem ---- 1b) fail loudly if AOT was skipped -----------------------------
rem If the AOT runtime pack is missing from the restore, the publish silently produces a
rem JIT build (coreclr.dll + OSTGUI.dll present, ~190 MB, OnlineHost back to a stub).
rem Catch that here instead of shipping it.
rem NOTE: no parenthesised if-block here on purpose - an unescaped ")" inside the block
rem aborts cmd with ". was unexpected at this time." (hit 2026-09-30).
if not exist "%OUT%\coreclr.dll" goto aotok
echo [FAIL] AOT was skipped: coreclr.dll is present, so this is a JIT build.
echo        Check that project.assets.json carries the NativeAOT runtime pack;
echo        restore must run with PublishAot=true and SelfContained=true.
exit /b 1

:aotok

rem ---- 2) the host ---------------------------------------------------
rem OnlineHost.exe is a SEPARATE process (Process.Start from the GUI),
rem so it must be published on its own. Publishing only the GUI leaves
rem the apphost stub without OnlineHost.dll: the host dies on start, the
rem caller is fire-and-forget, and the whole online feature (DLL inject +
rem AppID Changer) fails silently. See OSTGUI.csproj's ProjectReference.
rem Same two-step rule as the GUI above: restore with AOT properties, then publish.
"%MSB%" "%HOSTPROJ%" /t:Restore /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /nologo /v:m
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" goto fail
"%MSB%" "%HOSTPROJ%" /t:Publish /p:Configuration=Release /p:RuntimeIdentifier=win-x64 /p:SelfContained=true /p:PublishAot=true /p:PublishDir="%OUT%" /nologo /v:m
set "CODE=%ERRORLEVEL%"
if not "%CODE%"=="0" goto fail

rem ---- 3) publish-only leftovers -------------------------------------
if exist "%OUT%\OSTGUI.pdb" del /q "%OUT%\OSTGUI.pdb"
if exist "%OUT%\OnlineHost.pdb" del /q "%OUT%\OnlineHost.pdb"
rem Framework-dependent config from the GUI publish. A native host ignores it,
rem but leaving it around makes people think the host is still JIT.
if exist "%OUT%\OnlineHost.runtimeconfig.json" del /q "%OUT%\OnlineHost.runtimeconfig.json"
if exist "%OUT%\OnlineHost.deps.json" del /q "%OUT%\OnlineHost.deps.json"
if exist "%OUT%\OnlineHost.dll" del /q "%OUT%\OnlineHost.dll"

rem ---- 4) self check -------------------------------------------------
rem With no arguments the host validates its args and exits with 2. Anything
rem else means the host is broken again (e.g. a stub got copied over it).
start /wait "" "%OUT%\OnlineHost.exe"
set "HC=%ERRORLEVEL%"
if not "%HC%"=="2" goto hostbroken

echo.
echo [OK] exit=0
echo      host self-check: exit=2 (args rejected, as expected)
echo      output: %OUT%
exit /b 0

:hostbroken
echo.
echo [FAIL] host self-check returned %HC%, expected 2.
echo        The online feature (DLL inject / AppID Changer) would fail silently.
exit /b 1

:fail
echo.
echo [FAIL] exit=%CODE%
exit /b %CODE%
