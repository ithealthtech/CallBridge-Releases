@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-CallBridge.ps1"
set "exitCode=%ERRORLEVEL%"
if not "%exitCode%"=="0" (
    echo.
    echo CallBridge could not start. Review the message above, then press any key to close.
    pause >nul
)
endlocal & exit /b %exitCode%
