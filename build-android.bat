@echo off
REM 
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-android.ps1" %*
pause
