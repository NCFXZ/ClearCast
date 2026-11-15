@echo off
setlocal
for %%I in ("%~dp0..") do set "CLEARCAST_HOME=%%~fI"
set "CLEARCAST_APP_DIRECTORY=%CLEARCAST_HOME%\.runtime\app"
set "CLEARCAST_EXE=%CLEARCAST_APP_DIRECTORY%\ClearCast.exe"
if not exist "%CLEARCAST_EXE%" (
    echo ClearCast.exe is missing. See "%CLEARCAST_HOME%\README.md" for the build command.
    pause
    exit /b 1
)
if not exist "%CLEARCAST_APP_DIRECTORY%\ClearCast.dll" (
    echo ClearCast.dll is missing. Republish the complete app to "%CLEARCAST_APP_DIRECTORY%".
    pause
    exit /b 1
)
start "" /D "%CLEARCAST_APP_DIRECTORY%" "%CLEARCAST_EXE%"
exit /b %ERRORLEVEL%
