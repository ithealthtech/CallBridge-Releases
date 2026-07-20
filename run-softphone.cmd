@echo off
setlocal
set APP_DIR=%~dp0
if exist "%APP_DIR%publish\CallBridge.Desktop.exe" (
  start "" "%APP_DIR%publish\CallBridge.Desktop.exe"
) else (
  dotnet run --project "%APP_DIR%CallBridge.Desktop.csproj" --configuration Release
)
