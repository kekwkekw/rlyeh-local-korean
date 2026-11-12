@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Mode VerifyPackage
set "result=%errorlevel%"
pause
exit /b %result%
