@echo off
cd /d D:\Projects\OSTGUI
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64
MSBuild.exe OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /m /v:q
if %ERRORLEVEL% neq 0 (
    echo Build FAILED
    exit /b %ERRORLEVEL%
)
echo Build OK