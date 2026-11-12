@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1" -Mode Install %*
set "result=%errorlevel%"
echo.
if not "%result%"=="0" echo Installation stopped. Read the error above. No game was launched.
pause
exit /b %result%
