@echo off
setlocal

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\open-admin-panel.ps1" -Target Site
set "EXIT_CODE=%ERRORLEVEL%"

if "%EXIT_CODE%"=="0" exit /b 0

echo.
echo Could not open the private osu! website. Read the error above.
echo Press any key to close this window.
pause >nul
exit /b %EXIT_CODE%
