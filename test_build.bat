@echo off  
call "C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat" -arch=x64  
cd /d d:\Projects\OSTGUI  
"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" OSTGUI\OSTGUI.csproj /t:Build /p:Configuration=Debug  
