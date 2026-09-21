@echo off
call "%~dp0START_SERVER.bat" Stop
exit /b %ERRORLEVEL%
