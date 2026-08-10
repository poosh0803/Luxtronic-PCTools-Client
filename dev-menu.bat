@echo off
REM Double-click launcher for dev-menu.ps1 - bypasses the default PowerShell execution
REM policy (Restricted) that otherwise blocks the .ps1 from running with a "scripts is
REM disabled on this system" error, without changing any system-wide policy setting.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0dev-menu.ps1"
pause
