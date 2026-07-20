@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0CallBridge-Softphone.ps1"
if errorlevel 1 pause
