@echo off
cd /d "%~dp0"

rem === SETTINGS ===
set PROJ=proj
set APPNAME=MediaInfoGrabber
set TFM=net8.0-windows10.0.19041.0
set RID=win-x64

rem === CLEAN START ===
taskkill /IM "%APPNAME%.exe" /T /F
if exist ".\%PROJ%" rd /s /q ".\%PROJ%"


rem === BUILD & PUBLISH ===
dotnet new console -n "%PROJ%" --force
dotnet clean
dotnet restore
dotnet publish -c Release

rem === COPY EXE HERE ===
set PUB=.\bin\Release\%TFM%\%RID%\publish
if not exist "%PUB%\%APPNAME%.exe" goto :fail
del "..\." /F /Q
copy /y "%PUB%\%APPNAME%.exe" "..\%APPNAME%.exe" >nul || goto :fail

rem === CLEANUP ===
if exist ".\%PROJ%" rd /s /q ".\%PROJ%"
if exist ".\obj" rd /s /q ".\obj"
if exist ".\bin" rd /s /q ".\bin"

exit 0

:fail
echo EXE NICHT GEFUNDEN!