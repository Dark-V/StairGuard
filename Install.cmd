@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Build-and-Install.ps1"
if errorlevel 1 (
  echo.
  echo StairGuard installation failed. Read the error above.
  pause
)
