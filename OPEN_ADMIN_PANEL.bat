@echo off
setlocal

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\site\open-admin-panel.ps1"
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" exit /b 0

echo.
echo Could not open the private osu! admin panel. Read the error above.
echo Press any key to close this window.
pause >nul
exit /b %EXIT_CODE%
