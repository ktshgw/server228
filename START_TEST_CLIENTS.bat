@echo off
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0launcher\Start-TestClients.ps1" -Menu %*
if errorlevel 1 pause
