@echo off
REM Builds the standalone single-file exe: publish\win-x64\Luxtronic.PCTools.exe
REM The bundled test tools (tools\ folder) are embedded in the exe, so that is the only file to copy.
dotnet publish "%~dp0src\Luxtronic.PCTools\Luxtronic.PCTools.csproj" -c Release -r win-x64 --nologo -p:PublishProfile=Single
if errorlevel 1 (
    echo Publish failed.
    pause
    exit /b 1
)
echo.
echo Done: %~dp0publish\win-x64\Luxtronic.PCTools.exe
pause
