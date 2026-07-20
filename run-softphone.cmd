@echo off
setlocal
set APP_DIR=%~dp0
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%APP_DIR%Start-CallBridge.ps1"
