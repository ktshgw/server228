@echo off
setlocal
chcp 65001 >nul

set "SCRIPT_DIR=%~dp0"
set "ACTION=%~1"
if "%ACTION%"=="" set "ACTION=Start"

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT_DIR%scripts\easy-server.ps1" -Action "%ACTION%" -Username "%~2"
set "EXIT_CODE=%ERRORLEVEL%"

echo.
if "%EXIT_CODE%"=="2" (
    echo Server is healthy locally, but public HTTPS could not be verified from this PC.
    echo Check DNS/ports/firewall/CGNAT. If your router has no NAT loopback, test via mobile data.
) else if not "%EXIT_CODE%"=="0" (
    echo Server command failed. Read the message above.
) else (
    echo Done.
)
echo Press any key to close this window.
pause >nul
exit /b %EXIT_CODE%
