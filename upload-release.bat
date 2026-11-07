@echo off
REM Uploads publish\win-x64\Luxtronic.PCTools.exe to the server's Publish page with an automatic
REM version. Extra arguments are passed through, e.g.:
REM   upload-release.bat -Build                  (publish first, then upload)
REM   upload-release.bat -DryRun                 (show the version, upload nothing)
REM   upload-release.bat -Version 2.1.0 -Notes "New wizard"
REM   upload-release.bat -Server http://localhost:7777
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0upload-release.ps1" %*
if errorlevel 1 (
    pause
    exit /b 1
)
pause
