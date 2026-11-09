@echo off
setlocal
for %%I in ("%~dp0..") do set "CLEARCAST_HOME=%%~fI"
set "CLEARCAST_EXE=%CLEARCAST_HOME%\.runtime\app\ClearCast.exe"
if not exist "%CLEARCAST_EXE%" (
    echo ClearCast.exe is missing. See "%CLEARCAST_HOME%\README.md" for the build command.
    pause
    exit /b 1
)
start "" "%CLEARCAST_EXE%"
