@echo off
cd /d %~dp0
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64 >nul
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug /m