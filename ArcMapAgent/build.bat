@echo off
setlocal
cd /d %~dp0
dotnet build -c Release
if %errorlevel% neq 0 exit /b %errorlevel%
powershell -ExecutionPolicy Bypass -File pack.ps1
endlocal
