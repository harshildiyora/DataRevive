@echo off
setlocal
cd /d "%~dp0bin\Release\app"
start "" "%~dp0bin\Release\app\DataRecovery.App.exe" %*
exit /b 0
