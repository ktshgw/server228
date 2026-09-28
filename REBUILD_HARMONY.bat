@echo off
setlocal
chcp 65001 >nul

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\client\rebuild-harmony.ps1" %*
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if not "%EXIT_CODE%"=="0" (
    echo Harmony refresh or switcher staging failed. Read the error above.
) else (
    echo Harmony refreshed, hashes updated, and the switcher release staged.
)
echo Press any key to close this window.
pause >nul
exit /b %EXIT_CODE%
