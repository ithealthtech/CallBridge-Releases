@echo off
setlocal

set VERSION=0.14.0

powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File "%~dp0build-msi.ps1" ^
  -Configuration Release ^
  -Version "%VERSION%"

if errorlevel 1 (
    echo.
    echo CallBridge MSI build failed.
    pause
    exit /b 1
)

echo.
echo CallBridge MSI build completed.
pause
