@echo off
setlocal
set "CLEARCAST_HOME=%~dp0."
set "CLEARCAST_EXE=%~dp0.runtime\app\ClearCast.exe"
if not exist "%CLEARCAST_EXE%" (
    echo ClearCast.exe is missing. See README.md for the build command.
    pause
    exit /b 1
)
start "" "%CLEARCAST_EXE%"
