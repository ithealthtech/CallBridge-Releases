@echo off
setlocal

set VERSION=1.0.0

powershell.exe -NoProfile -ExecutionPolicy Bypass ^
  -File "%~dp0build-installer.ps1" ^
  -Configuration Release ^
  -Runtime win-x64 ^
  -Version "%VERSION%" ^
  -SelfContained

if errorlevel 1 (
    echo.
    echo CallBridge installer build failed.
    pause
    exit /b 1
)

echo.
echo CallBridge installer build completed.
pause