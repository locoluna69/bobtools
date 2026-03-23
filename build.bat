@echo off
echo Starting PowerShell build script...
powershell -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if %ERRORLEVEL% neq 0 (
    echo.
    echo Build failed.
    exit /b %ERRORLEVEL%
)
echo.
pause
