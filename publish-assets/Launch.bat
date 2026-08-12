@echo off
REM Double-click entry point for a technician on the target PC. Bypasses PowerShell's default
REM execution policy (Restricted) the same way dev-menu.bat does, then runs Launch.ps1's
REM pre-flight checks before launching the app.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Launch.ps1"
